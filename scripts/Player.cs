
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

		flashlight = GetNode<SpotLight3D>("Head/SpotLight3D");
		

		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
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

		if (@event.IsActionPressed("ui_cancel"))
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}

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
}
