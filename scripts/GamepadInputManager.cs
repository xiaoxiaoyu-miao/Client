using System;
using Godot;

/// <summary>
/// Drives the client with a gamepad by synthesising mouse events, so gameplay aiming and the
/// menus both go through the code paths they already use for a real mouse.
/// <para>
/// On Android there is no hardware cursor, which is exactly why this works: the stick moves a
/// virtual cursor, and <see cref="Input.ParseInputEvent"/> feeds it (and the clicks) into the
/// normal input pipeline.
/// </para>
/// </summary>
public partial class GamepadInputManager : Node
{
    /// <summary>How the aim stick drives the in-game cursor.</summary>
    public enum AimMapping
    {
        /// <summary>
        /// Stick position *is* the cursor position on the grid, optionally snapped to cell
        /// centers. This is what an aim game on a 3x3 grid wants.
        /// </summary>
        Absolute,

        /// <summary>Stick deflection is velocity, like a twin-stick shooter.</summary>
        Velocity,
    }

    [Export]
    public bool Enabled = true;

    /// <summary>Preferred joypad device index; falls back to whatever answers first.</summary>
    [Export]
    public int PreferredDevice = 0;

    /// <summary>Aim with the right stick. The left stick still works as a fallback.</summary>
    [Export]
    public bool AimWithRightStick = true;

    /// <summary>
    /// Snap the absolute aim target to the nearest cell center, turning the stick into a
    /// 3x3 position selector. This is what makes hits feel solid instead of fuzzy.
    /// </summary>
    [Export]
    public bool SnapToCells = true;

    /// <summary>Grid cells per side used for snapping (the play field is 3x3).</summary>
    [Export]
    public int GridCells = 3;

    [Export]
    public AimMapping Mapping = AimMapping.Absolute;

    /// <summary>Absolute-mode correction speed, in mouse pixels per second per grid unit.</summary>
    [Export]
    public float AbsoluteGain = 3600f;

    /// <summary>Menu cursor speed in mouse pixels per second at full deflection.</summary>
    [Export]
    public float StickSpeed = 900f;

    [Export]
    public float Deadzone = 0.18f;

    [Export]
    public float ResponseExponent = 2.0f;

    [Export]
    public bool ScrollWithRightStick = true;

    private const float ScrollThreshold = 0.55f;
    private const double ScrollInterval = 0.12;
    private const int ProbeDeviceCount = 8;

    private int device = -1;
    private double scrollCooldown;
    private double announcementCooldown;
    private Vector2 cursor;
    private bool cursorReady;
    private bool leftHeld;
    private bool pauseHeld;
    private bool skipHeld;
    private bool backHeld;
    private bool announced;

    public bool HasGamepad => device >= 0;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        device = findDevice();
    }

    public override void _Process(double delta)
    {
        if (!Enabled)
        {
            return;
        }

        refreshCursor();

        if (device < 0)
        {
            announce(delta, "no joypad reported yet; probing ids");
            device = probeForActivity();
        }

        if (device < 0)
        {
            return;
        }

        var aim = readAim();

        if (aim != Vector2.Zero)
        {
            var move = composeMove(aim, (float)delta);

            if (move != Vector2.Zero)
            {
                moveCursor(move);
            }
        }
        else if (ScrollWithRightStick)
        {
            scroll(delta);
        }

        handleButtons();
    }

    private void announce(double delta, string message)
    {
        if (announced)
        {
            return;
        }

        announcementCooldown -= delta;

        if (announcementCooldown > 0)
        {
            return;
        }

        announcementCooldown = 5;
        GD.Print($"[Gamepad] {message}");
    }

    /// <summary>
    /// Some backends only register a pad once it sends input, so fall back to asking each id
    /// directly whether anything is held or deflected.
    /// </summary>
    private int probeForActivity()
    {
        for (int id = 0; id < ProbeDeviceCount; id++)
        {
            for (int axis = 0; axis < 8; axis++)
            {
                if (Mathf.Abs(Input.GetJoyAxis(id, (JoyAxis)axis)) > Deadzone)
                {
                    return adopt(id, "axis activity");
                }
            }

            for (int button = 0; button < 21; button++)
            {
                if (Input.IsJoyButtonPressed(id, (JoyButton)button))
                {
                    return adopt(id, "button press");
                }
            }
        }

        return -1;
    }

    private int adopt(int id, string reason)
    {
        GD.Print($"[Gamepad] using device {id} ({reason})");
        announced = true;
        return id;
    }

    private int findDevice()
    {
        var connected = Input.GetConnectedJoypads();

        if (connected.Count == 0)
        {
            return -1;
        }

        foreach (var joypad in connected)
        {
            if (joypad == PreferredDevice)
            {
                return adopt(joypad, "preferred id");
            }
        }

        return adopt(connected[0], "first connected pad");
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
        {
            cursor = GetViewport().GetMousePosition();
        }
    }

    /// <summary>Turns a normalized aim direction into a mouse-pixel delta for this frame.</summary>
    private Vector2 composeMove(Vector2 aim, float delta)
    {
        if (Mapping == AimMapping.Absolute && tryGetCursor(out var cursorWorld))
        {
            var target = new Vector2(aim.X, -aim.Y) * Constants.BOUNDS;

            if (SnapToCells && GridCells > 1)
            {
                target = snapToCell(target);
            }

            var error = target - cursorWorld;

            // Screen Y grows downwards while the play field grows upwards, so flip Y.
            return new Vector2(error.X, -error.Y) * (AbsoluteGain * delta);
        }

        return aim * (StickSpeed * delta);
    }

    /// <summary>Rounds a play-field position onto the nearest cell center.</summary>
    private Vector2 snapToCell(Vector2 position)
    {
        // The play field spans [-BOUNDS, BOUNDS] and holds GridCells cells per side, so cell
        // centers sit a whole cell apart with the middle one at zero.
        float spacing = Constants.BOUNDS.X * 2f / Mathf.Max(GridCells - 1, 1);
        var snapped = new Vector2(Mathf.Round(position.X / spacing), Mathf.Round(position.Y / spacing)) * spacing;
        return snapped.Clamp(-Constants.BOUNDS, Constants.BOUNDS);
    }

    private static bool tryGetCursor(out Vector2 cursorWorld)
    {
        cursorWorld = Vector2.Zero;

        var game = Game.Instance;

        if (game is not { } running || !running.IsInsideTree() || running.Runner is not { Playing: true } || Game.Attempt is null)
        {
            return false;
        }

        cursorWorld = Game.Attempt.CursorPosition;
        return cursorWorld.IsFinite();
    }

    private Vector2 readAim()
    {
        var right = new Vector2(Input.GetJoyAxis(device, JoyAxis.RightX), Input.GetJoyAxis(device, JoyAxis.RightY));
        var left = new Vector2(Input.GetJoyAxis(device, JoyAxis.LeftX), Input.GetJoyAxis(device, JoyAxis.LeftY));

        // Prefer the configured stick, but accept the other one so a single-stick pad still works.
        var stick = AimWithRightStick ? right : left;
        var fallback = AimWithRightStick ? left : right;

        if (stick.Length() <= Deadzone && fallback.Length() > Deadzone)
        {
            stick = fallback;
        }

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
        {
            return Vector2.Zero;
        }

        if (direction.Length() > 1)
        {
            direction = direction.Normalized();
        }

        var magnitude = direction.Length();

        if (magnitude <= Deadzone)
        {
            return Vector2.Zero;
        }

        var curved = Mathf.Pow(Mathf.Min((magnitude - Deadzone) / (1 - Deadzone), 1), ResponseExponent);

        return direction / magnitude * curved;
    }

    private void scroll(double delta)
    {
        scrollCooldown -= delta;

        if (scrollCooldown > 0)
        {
            return;
        }

        // In menus the right stick aims, so scrolling uses the other stick's vertical axis.
        var wheel = Input.GetJoyAxis(device, AimWithRightStick ? JoyAxis.LeftY : JoyAxis.RightY);

        if (Mathf.Abs(wheel) > ScrollThreshold)
        {
            sendWheel(wheel < 0);
            scrollCooldown = ScrollInterval;
        }
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
