using System;
using Godot;

/// <summary>
/// Feeds a connected gamepad into the client as if it were a mouse.
/// <para>
/// Rhythia reads aim as a relative mouse delta (<c>InputEventMouseMotion.Relative</c>) and
/// reads the menus as a cursor position plus clicks, so the whole game can be driven by
/// synthesising those events with <see cref="Input.ParseInputEvent"/>.
/// </para>
/// <para>
/// This matters on Android, where there is no hardware cursor at all: nothing in the
/// gameplay or the UI code has to change, and the same build stays playable with a
/// real mouse/keyboard on desktop.
/// </para>
/// </summary>
public partial class GamepadInputManager : Node
{
    /// <summary>How the aim stick drives the in-game cursor.</summary>
    public enum AimMapping
    {
        /// <summary>
        /// The stick position *is* the cursor position on the grid (stick2mouse style,
        /// what a 3x3 aim game wants). Centering the stick holds the cursor in place.
        /// </summary>
        Absolute,

        /// <summary>Stick deflection is velocity, like any twin-stick shooter.</summary>
        Velocity,
    }

    /// <summary>Set to false to ignore the gamepad entirely (e.g. touch-only play).</summary>
    [Export]
    public bool Enabled = true;

    /// <summary>Preferred joypad device index; falls back to the first connected pad.</summary>
    [Export]
    public int PreferredDevice = 0;

    /// <summary>Aim with the right stick instead of the left one.</summary>
    [Export]
    public bool AimWithRightStick = false;

    /// <summary>Absolute or velocity aiming; the mode used while a map is playing.</summary>
    [Export]
    public AimMapping Mapping = AimMapping.Absolute;

    /// <summary>
    /// Correction speed for <see cref="AimMapping.Absolute"/>, in mouse pixels per second
    /// per grid unit of distance between the cursor and the stick. The loop is closed on
    /// the real cursor position, so this stays correct at any sensitivity setting.
    /// </summary>
    [Export]
    public float AbsoluteGain = 2400f;

    /// <summary>
    /// Cursor travel in mouse pixels per second at full stick deflection.
    /// The game divides this by the player's sensitivity (default 0.5) and by 120,
    /// so ~900 crosses the whole 3x3 grid in roughly 0.7 s.
    /// </summary>
    [Export]
    public float StickSpeed = 900f;

    /// <summary>Stick travel ignored before the cursor starts moving.</summary>
    [Export]
    public float Deadzone = 0.15f;

    /// <summary>
    /// Response curve exponent. Values above 1 give finer control near the center and
    /// still allow full-speed sweeps, which is what an aim game wants.
    /// </summary>
    [Export]
    public float ResponseExponent = 2.0f;

    /// <summary>Right stick scrolls menus (wheel up/down) while the aim stick is idle.</summary>
    [Export]
    public bool ScrollWithRightStick = true;

    private const float ScrollThreshold = 0.55f;
    private const double ScrollInterval = 0.12;
    private const double DeviceRefreshInterval = 0.5;

    private int device = -1;
    private double sinceDeviceRefresh;
    private double scrollCooldown;
    private Vector2 cursor;
    private bool cursorReady;
    private bool leftHeld;
    private bool pauseHeld;
    private bool skipHeld;
    private bool backHeld;

    public bool HasGamepad => device >= 0;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        device = findDevice();
    }

    public override void _Process(double delta)
    {
        if (!Enabled)
            return;

        sinceDeviceRefresh += delta;

        if (sinceDeviceRefresh >= DeviceRefreshInterval)
        {
            sinceDeviceRefresh = 0;
            device = findDevice();
        }

        if (device < 0)
            return;

        refreshCursor();

        var aim = readAim();

        if (aim != Vector2.Zero)
        {
            var move = composeMove(aim, (float)delta);

            if (move != Vector2.Zero)
                moveCursor(move);
        }
        else if (ScrollWithRightStick && !AimWithRightStick)
        {
            scrollCooldown -= delta;

            if (scrollCooldown <= 0)
            {
                var wheel = Input.GetJoyAxis(device, JoyAxis.RightY);

                if (Mathf.Abs(wheel) > ScrollThreshold)
                {
                    sendWheel(wheel < 0);
                    scrollCooldown = ScrollInterval;
                }
            }
        }

        handleButtons();
    }

    /// <summary>
    /// Turns a normalized aim direction into a mouse-pixel delta for this frame.
    /// <para>
    /// In absolute mode the delta steers the real cursor towards the grid position the
    /// stick is pointing at, so the stick behaves like a position rather than a speed and
    /// the mapping self-corrects whatever sensitivity the player picked.
    /// </para>
    /// </summary>
    private Vector2 composeMove(Vector2 aim, float delta)
    {
        if (Mapping == AimMapping.Absolute && tryGetCursor(out var cursorWorld))
        {
            var target = new Vector2(aim.X, -aim.Y) * Constants.BOUNDS;
            var error = target - cursorWorld;

            // Screen Y grows downwards while the play field grows upwards, so flip Y.
            return new Vector2(error.X, -error.Y) * (AbsoluteGain * delta);
        }

        return aim * (StickSpeed * delta);
    }

    /// <summary>Cursor position of the running attempt, in play-field units.</summary>
    private static bool tryGetCursor(out Vector2 cursorWorld)
    {
        cursorWorld = Vector2.Zero;

        var game = Game.Instance;

        if (game is not { } running || !running.IsInsideTree() || running.Runner is not { Playing: true } || Game.Attempt is null)
            return false;

        cursorWorld = Game.Attempt.CursorPosition;
        return cursorWorld.IsFinite();
    }

    private int findDevice()
    {
        var connected = Input.GetConnectedJoypads();

        if (connected.Count == 0)
            return -1;

        foreach (var joypad in connected)
        {
            if (joypad == PreferredDevice)
                return joypad;
        }

        return connected[0];
    }

    private void refreshCursor()
    {
        var size = GetViewport().GetVisibleRect().Size;

        if (!cursorReady)
        {
            cursor = size / 2;
            cursorReady = true;
        }

        // Track a real mouse on desktop; on Android the reported touch position is
        // meaningless for us, so trust the cursor we accumulate ourselves.
        if (OS.GetName() != "Android")
            cursor = GetViewport().GetMousePosition();
    }

    /// <summary>Normalized aim direction, deadzone-corrected and response-curved.</summary>
    private Vector2 readAim()
    {
        var stick = new Vector2(
            Input.GetJoyAxis(device, AimWithRightStick ? JoyAxis.RightX : JoyAxis.LeftX),
            Input.GetJoyAxis(device, AimWithRightStick ? JoyAxis.RightY : JoyAxis.LeftY)
        );

        // D-pad acts as full-deflection aiming, handy for lining up on the grid.
        var dpad = Vector2.Zero;

        if (Input.IsJoyButtonPressed(device, JoyButton.DpadLeft))
            dpad.X -= 1;
        if (Input.IsJoyButtonPressed(device, JoyButton.DpadRight))
            dpad.X += 1;
        if (Input.IsJoyButtonPressed(device, JoyButton.DpadUp))
            dpad.Y -= 1;
        if (Input.IsJoyButtonPressed(device, JoyButton.DpadDown))
            dpad.Y += 1;

        var direction = stick + dpad;

        if (direction == Vector2.Zero)
            return Vector2.Zero;

        // Stick and d-pad can overlap; keep the result within the unit circle.
        if (direction.Length() > 1)
            direction = direction.Normalized();

        var magnitude = direction.Length();

        if (magnitude <= Deadzone)
            return Vector2.Zero;

        var curved = Mathf.Pow(Mathf.Min((magnitude - Deadzone) / (1 - Deadzone), 1), ResponseExponent);

        return direction / magnitude * curved;
    }

    private void handleButtons()
    {
        if (Input.IsJoyButtonPressed(device, JoyButton.A) != leftHeld)
        {
            leftHeld = !leftHeld;
            sendClick(leftHeld);
        }

        if (Input.IsJoyButtonPressed(device, JoyButton.Start) != pauseHeld)
        {
            pauseHeld = !pauseHeld;
            sendKey(Key.Escape, pauseHeld);
        }

        if (Input.IsJoyButtonPressed(device, JoyButton.B) != backHeld)
        {
            backHeld = !backHeld;
            sendKey(Key.Escape, backHeld);
        }

        if (Input.IsJoyButtonPressed(device, JoyButton.X) != skipHeld)
        {
            skipHeld = !skipHeld;
            sendKey(Key.Space, skipHeld);
        }
    }

    private void moveCursor(Vector2 move)
    {
        var size = GetViewport().GetVisibleRect().Size;
        cursor = (cursor + move).Clamp(Vector2.Zero, size);

        Input.ParseInputEvent(
            new InputEventMouseMotion
            {
                Position = cursor,
                GlobalPosition = cursor,
                Relative = move,
                ScreenRelative = move,
                Velocity = move,
                ScreenVelocity = move,
            }
        );
    }

    private void sendClick(bool pressed) =>
        Input.ParseInputEvent(
            new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
                Position = cursor,
                GlobalPosition = cursor,
                ButtonMask = pressed ? MouseButtonMask.Left : 0,
            }
        );

    private void sendWheel(bool up) =>
        Input.ParseInputEvent(
            new InputEventMouseButton
            {
                ButtonIndex = up ? MouseButton.WheelUp : MouseButton.WheelDown,
                Pressed = true,
                Position = cursor,
                GlobalPosition = cursor,
            }
        );

    private static void sendKey(Key key, bool pressed) =>
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });
}
