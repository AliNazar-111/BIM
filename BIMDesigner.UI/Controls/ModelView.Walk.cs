using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;
using MediaPoint3D = System.Windows.Media.Media3D.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Walking through the model, and playing a walkthrough. Walking, the eye stands a person's
/// height above whatever is underfoot - a floor, a stair's treads, a ramp, the ground - and goes
/// where the keys take it: W, A, S, D or the arrows, Shift to hurry, a drag to look round, Esc
/// to stop. Walls, glass and railings stop it; doors do not. A walkthrough is played at its own
/// frame rate along its path.
/// </summary>
public partial class ModelView
{
    private const double WalkSpeed = 1400;
    private const double RunSpeed = 4000;
    private const double TurnSpeed = 90;

    private bool _walking;
    private CorePoint3D _eye;
    private double _feet;
    private double _lookYaw;
    private double _lookPitch;
    private readonly HashSet<Key> _held = new();
    private DateTime _lastStep;
    private bool _stepping;

    private Walkthrough? _playing;
    private DateTime _playStarted;
    private int _playFrom;

    /// <summary>The frame of a walkthrough shown, held until the view is walked, changed or played.</summary>
    private Camera3D? _frameCamera;

    /// <summary>Whether the view is being walked through.</summary>
    public bool IsWalking => _walking;

    /// <summary>The walkthrough being played, or null.</summary>
    public Walkthrough? Playing => _playing;

    /// <summary>Raised when walking starts or stops, or a walkthrough starts or stops playing.</summary>
    public event EventHandler? MovingChanged;

    /// <summary>Raised with each frame of a walkthrough as it is shown.</summary>
    public event EventHandler<int>? FrameShown;

    /// <summary>
    /// Starts walking. From a camera standing at eye height over a floor, from there; otherwise
    /// a few paces back from whatever is in the middle of the view, on the floor there, looking at it.
    /// Null when started; the reason when it cannot be.
    /// </summary>
    public string? StartWalking()
    {
        if (_elevation is not null) return "An elevation is not walked through: open a 3D view.";
        if (_document is null) return "There is nothing to walk through.";
        StopMoving();

        var eye = new CorePoint3D(_camera.Position.X * MillimetresPerUnit, _camera.Position.Y * MillimetresPerUnit, _camera.Position.Z * MillimetresPerUnit);
        var look = _camera.LookDirection;
        if (IsOrtho) eye = new CorePoint3D((_target.X - look.X) * MillimetresPerUnit, (_target.Y - look.Y) * MillimetresPerUnit, (_target.Z - look.Z) * MillimetresPerUnit);
        var heading = Math.Atan2(look.Y, look.X) * 180 / Math.PI;

        double? feet = null;
        var stand = new Point2D(eye.X, eye.Y);

        // A camera at eye height over a floor: stand where it stands. A camera's view with nothing
        // modelled under it - out on the ground, with no ground drawn - stands there all the same.
        if (Walking.FloorUnder(_allMeshes, stand, eye.Z - Views3D.EyeHeight) is { } under && eye.Z - under is > 600 and < 2600)
        {
            feet = under;
        }
        else if (!IsOrtho && _document is { } document && document.Elements.Contains(_view) && Walking.FloorUnder(_allMeshes, stand, eye.Z) is null)
        {
            feet = eye.Z - Views3D.EyeHeight;
        }
        else if (HitTest(new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2)) is { } hit)
        {
            // Back from what is looked at, toward the eye, on the floor there.
            var back = new Vector2D(eye.X - hit.At.X, eye.Y - hit.At.Y).NormalisedOrDefault(Vector2D.UnitX);
            stand = new Point2D(hit.At.X, hit.At.Y) + back * 2500;
            feet = Walking.FloorUnder(_allMeshes, stand, hit.At.Z + 100) ?? Walking.FloorUnder(_allMeshes, new Point2D(hit.At.X, hit.At.Y), hit.At.Z + 100) ?? hit.At.Z;
            heading = Math.Atan2(-back.Y, -back.X) * 180 / Math.PI;
        }

        if (feet is not { } standing) return "Look at the building first: walking starts a few paces back from what is in the middle of the view.";

        return Walk(stand, standing, heading);
    }

    private Guid? _walkingAs;

    /// <summary>The person in the model being walked as: not drawn while they are, being the eyes the view is seen through.</summary>
    public Guid? WalkingAs
    {
        get => _walkingAs;
        set
        {
            if (_walkingAs == value) return;
            _walkingAs = value;
            Rebuild();
        }
    }

    /// <summary>Where the last walk ended - the feet, and the way they faced.</summary>
    public (CorePoint3D Feet, double Heading)? LastWalk { get; private set; }

    /// <summary>Who is walking: their eye height, the steps and slopes they can take, how wide they are.</summary>
    public Walker Walker { get; set; } = Walker.For(WalkerKind.Adult, false);

    /// <summary>Where the walker stands - their feet - and which way they face, while walking.</summary>
    public (CorePoint3D Feet, double Heading)? WalkerAt => _walking ? (new CorePoint3D(_eye.X, _eye.Y, _feet), _lookYaw) : null;

    /// <summary>Raised with why a step was not taken: a wall in the way, a step too high for a wheelchair, a slope too steep.</summary>
    public event EventHandler<string>? WalkStopped;

    private string? _stoppedBy;

    /// <summary>Starts walking from a place - feet at a height, facing a way - as whoever <see cref="Walker"/> is.</summary>
    public string? StartWalkingAt(Point2D at, double feet, double heading)
    {
        if (_elevation is not null) return "An elevation is not walked through: open a 3D view.";
        if (_document is null) return "There is nothing to walk through.";
        StopMoving();
        return Walk(at, feet, heading);
    }

    private string? Walk(Point2D stand, double standing, double heading)
    {
        _frameCamera = null;
        _stoppedBy = null;
        (_walking, _feet, _lookYaw, _lookPitch) = (true, standing, heading, 0);
        _eye = new CorePoint3D(stand.X, stand.Y, standing + Walker.EyeHeight);
        _viewport.Camera = _camera;
        _camera.FieldOfView = 60;
        RefreshGrips();
        UpdateCamera();
        BuildEdges();
        Focus();
        MovingChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Stops walking: the view goes back to turning round a point, the one walked up to.</summary>
    public void StopWalking()
    {
        if (!_walking) return;
        LastWalk = (new CorePoint3D(_eye.X, _eye.Y, _feet), _lookYaw);
        _walking = false;
        _held.Clear();

        var look = WalkLook();
        _target = new MediaPoint3D(_eye.X / MillimetresPerUnit + look.X * 5, _eye.Y / MillimetresPerUnit + look.Y * 5, _eye.Z / MillimetresPerUnit + look.Z * 5);
        (_distance, _yaw, _pitch) = (5, _lookYaw + 180, Math.Clamp(-_lookPitch, -MaxPitch, MaxPitch));
        _camera.FieldOfView = _view.Camera?.FieldOfView ?? FieldOfView;
        _viewport.Camera = IsOrtho ? _ortho : _camera;
        RefreshGrips();
        UpdateCamera();
        BuildEdges();
        MovingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops walking and playing, whichever is going on.</summary>
    public void StopMoving()
    {
        StopWalking();
        StopPlaying();
        _frameCamera = null;
    }

    private Vector3D WalkLook()
    {
        var (yaw, pitch) = (_lookYaw * Math.PI / 180, _lookPitch * Math.PI / 180);
        return new Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
    }

    /// <summary>The camera at the eye, looking where the walker looks; kept in the view as a camera of its own.</summary>
    private void UpdateWalkCamera()
    {
        var look = WalkLook();
        _camera.Position = ToUnits(_eye);
        _camera.LookDirection = look;
        _camera.NearPlaneDistance = 0.05;
        _camera.FarPlaneDistance = 1500;

        var ahead = new CorePoint3D(_eye.X + look.X * 5000, _eye.Y + look.Y * 5000, _eye.Z + look.Z * 5000);
        _shownCamera = _view.Camera = new Camera3D(_eye, ahead, true) { FieldOfView = _camera.FieldOfView };
        CameraChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A drag while walking: looking round, and up and down.</summary>
    private void Look(Vector delta)
    {
        _lookYaw -= delta.X * 0.25;
        _lookPitch = Math.Clamp(_lookPitch - delta.Y * 0.25, -80, 80);
        UpdateCamera();
    }

    private static bool IsWalkKey(Key key) =>
        key is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right or Key.Q or Key.E;

    /// <summary>A key pressed while walking: held, and the walker moved every frame until it is let go.</summary>
    private bool WalkKeyDown(Key key)
    {
        if (!_walking) return false;
        if (key == Key.Escape)
        {
            StopWalking();
            return true;
        }

        if (!IsWalkKey(key)) return false;
        _held.Add(key);
        if (!_stepping)
        {
            _stepping = true;
            _lastStep = DateTime.Now;
            CompositionTarget.Rendering += OnWalkFrame;
        }

        return true;
    }

    private bool WalkKeyUp(Key key) => _walking && _held.Remove(key);

    private void OnWalkFrame(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var seconds = Math.Min((now - _lastStep).TotalSeconds, 0.1);
        _lastStep = now;

        if (!_walking || _held.Count == 0 || !IsKeyboardFocusWithin)
        {
            _held.Clear();
            _stepping = false;
            CompositionTarget.Rendering -= OnWalkFrame;
            return;
        }

        // Turning, with the arrows to the sides.
        if (_held.Contains(Key.Left)) _lookYaw += TurnSpeed * seconds;
        if (_held.Contains(Key.Right)) _lookYaw -= TurnSpeed * seconds;

        var forward = new Vector2D(Math.Cos(_lookYaw * Math.PI / 180), Math.Sin(_lookYaw * Math.PI / 180));
        var left = new Vector2D(-forward.Y, forward.X);
        var move = new Vector2D(0, 0);
        if (_held.Contains(Key.W) || _held.Contains(Key.Up)) move += forward;
        if (_held.Contains(Key.S) || _held.Contains(Key.Down)) move -= forward;
        if (_held.Contains(Key.A) || _held.Contains(Key.Q)) move += left;
        if (_held.Contains(Key.D) || _held.Contains(Key.E)) move -= left;

        if (move.Length > 1e-9)
        {
            var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? RunSpeed : WalkSpeed;
            Step(move.NormalisedOrDefault(forward) * (speed * seconds));
        }

        UpdateCamera();
    }

    /// <summary>
    /// A stride, if nothing is in the way and the walker can take it: the feet onto whatever is
    /// underfoot where it lands - not up or down a step higher than they can manage, nor up or
    /// down a slope steeper than they can.
    /// </summary>
    private void Step(Vector2D stride)
    {
        var from = new Point2D(_eye.X, _eye.Y);
        var to = from + stride;
        var walker = Walker;
        if (Walking.Blocked(_allMeshes, from, to, _feet, walker.EyeHeight, walker.Width))
        {
            Stopped(walker.Width > 0 ? $"Something is in the way of {walker.Name} {Core.Units.FormatMm(walker.Width)} wide - a wall, a narrow doorway, furniture." : "Something is in the way.");
            return;
        }

        var next = Walking.FloorUnder(_allMeshes, to, _feet, walker.StepUp) ?? _feet;
        if (walker.Kind == WalkerKind.WheelchairUser)
        {
            var higher = Walking.FloorUnder(_allMeshes, to, _feet, Walking.StepUp);
            var rise = (higher ?? next) - _feet;
            var us = _document?.ProjectInformation.ClearanceStandard.IsUs() == true;
            var rule = us ? "the ADA allows 1/2 in, bevelled, at a change of level (303.3)" : "Approved Document M allows a threshold of 15 mm at most";
            if (rise > walker.StepUp + 0.5)
            {
                Stopped($"A step up of {Core.Units.Millimetres(rise, "0")}: {walker.Name} cannot get up it - {rule}. A ramp, or a level threshold, is needed.");
                return;
            }

            if (next < _feet - walker.StepUp - 0.5)
            {
                Stopped($"A step down of {Core.Units.Millimetres(_feet - next, "0")}: {walker.Name} cannot get down it - {rule}.");
                return;
            }

            if (Math.Abs(next - _feet) / Math.Max(stride.Length, 1) > walker.Steepest + 0.004)
            {
                Stopped($"Steeper than 1:12: too steep for {walker.Name} - the most a ramp may be ({(us ? "ADA 405.2" : "Approved Document M")}).");
                return;
            }
        }

        _stoppedBy = null;
        _feet = next;
        _eye = new CorePoint3D(to.X, to.Y, _feet + walker.EyeHeight);
    }

    private void Stopped(string why)
    {
        if (why == _stoppedBy) return;
        _stoppedBy = why;
        WalkStopped?.Invoke(this, why);
    }

    /// <summary>The wheel while walking: a stride forward or back.</summary>
    private void WalkWheel(int delta)
    {
        var forward = new Vector2D(Math.Cos(_lookYaw * Math.PI / 180), Math.Sin(_lookYaw * Math.PI / 180));
        Step(forward * (delta > 0 ? 600 : -600));
        UpdateCamera();
    }

    // ---- walkthroughs -----------------------------------------------------------------

    /// <summary>Plays a walkthrough from a frame: the camera along its path at its own speed, looking the way it goes.</summary>
    public void Play(Walkthrough walk, int fromFrame = 0)
    {
        if (_document is null || walk.Path.Count < 2) return;
        StopWalking();
        StopPlaying();

        (_playing, _playStarted, _playFrom) = (walk, DateTime.Now, Math.Clamp(fromFrame, 0, walk.Frames - 1));
        _viewport.Camera = _camera;
        RefreshGrips();
        CompositionTarget.Rendering += OnPlayFrame;
        MovingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops a walkthrough where it has got to.</summary>
    public void StopPlaying()
    {
        if (_playing is null) return;
        _playing = null;
        CompositionTarget.Rendering -= OnPlayFrame;
        RefreshGrips();
        MovingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows one frame of a walkthrough, without playing it.</summary>
    public void ShowFrame(Walkthrough walk, int frame)
    {
        if (_document is null) return;
        _frameCamera = walk.FrameAt(_document, frame);
        _viewport.Camera = _camera;
        ShowCamera(_frameCamera);
        FrameShown?.Invoke(this, frame);
    }

    private void OnPlayFrame(object? sender, EventArgs e)
    {
        if (_playing is not { } walk || _document is null) return;

        var frame = _playFrom + (int)((DateTime.Now - _playStarted).TotalSeconds * walk.FramesPerSecond);
        var last = frame >= walk.Frames - 1;
        frame = Math.Min(frame, walk.Frames - 1);
        _frameCamera = walk.FrameAt(_document, frame);
        ShowCamera(_frameCamera);
        FrameShown?.Invoke(this, frame);
        if (last) StopPlaying();
    }

    /// <summary>
    /// The view as it is now, as a picture: what a walkthrough's frames are exported from. Its sides
    /// made even, as a video's frames are.
    /// </summary>
    public System.Windows.Media.Imaging.BitmapSource RenderFrame()
    {
        var width = Math.Max(2, (int)ActualWidth / 2 * 2);
        var height = Math.Max(2, (int)ActualHeight / 2 * 2);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The eye put where a camera is, looking where it looks.</summary>
    private void ShowCamera(Camera3D camera)
    {
        _camera.FieldOfView = camera.FieldOfView;
        _camera.Position = ToUnits(camera.Eye);
        _camera.LookDirection = ToUnits(camera.Target) - ToUnits(camera.Eye);
        _camera.NearPlaneDistance = 0.05;
        _camera.FarPlaneDistance = 1500;
        _gripOverlay?.InvalidateVisual();
        CameraChanged?.Invoke(this, EventArgs.Empty);
    }
}
