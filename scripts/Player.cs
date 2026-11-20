
using Godot;

public partial class Player : CharacterBody3D
{
	[Export] public float Speed = 5.0f;
	[Export] public float MouseSensitivity = 0.002f;

	private SpotLight3D flashlight;
	private Node3D head;

	public override void _Ready()
	{
		head = GetNode<Node3D>("Head");
		flashlight = GetNode<SpotLight3D>("Head/Flashlight");

		Input.MouseMode = Input.MouseModeEnum.Captured;

		GD.Print("Player initialized");
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey key &&
			key.Pressed &&
			!key.Echo &&
			key.Keycode == Key.F11)
		{
			ToggleFullscreen();
			return;
		}

		if (@event.IsActionPressed("toggle_flashlight"))
		{
			flashlight.Visible = !flashlight.Visible;
			return;
		}
		if (@event is InputEventMouseMotion mouse &&
			Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			RotateY(-mouse.Relative.X * MouseSensitivity);

			head.RotateX(-mouse.Relative.Y * MouseSensitivity);

			Vector3 rotation = head.Rotation;

			rotation.X = Mathf.Clamp(
				rotation.X,
				Mathf.DegToRad(-89),
				Mathf.DegToRad(89)
			);

			head.Rotation = rotation;
		}

		// Release mouse with Escape
		if (@event.IsActionPressed("ui_cancel"))
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}

		// Capture mouse with left click
		if (@event is InputEventMouseButton click &&
			click.Pressed &&
			click.ButtonIndex == MouseButton.Left)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		Vector3 velocity = Velocity;

		// Apply gravity
		if (!IsOnFloor())
		{
			velocity += GetGravity() * (float)delta;
		}
		Vector2 input = Input.GetVector(
			"move_left",
			"move_right",
			"move_forward",
            "move_backward"
		);

		Vector3 direction = Transform.Basis *
			new Vector3(input.X, 0, input.Y);

		velocity.X = direction.X * Speed;
		velocity.Z = direction.Z * Speed;

		Velocity = velocity;

		MoveAndSlide();
	}

	private void ToggleFullscreen()
	{
		Window window = GetWindow();

		if (window.Mode == Window.ModeEnum.Fullscreen ||
			window.Mode == Window.ModeEnum.ExclusiveFullscreen)
		{
			window.Mode = Window.ModeEnum.Windowed;
		}
		else
		{
			window.Mode = Window.ModeEnum.Fullscreen;
		}
	}

}
