using Godot;

public partial class Player : CharacterBody3D
{
	[Export] public float Speed = 5f;
	[Export] public float MouseSensitivity = 0.002f;

	private SpotLight3D flashlight;
	private Node3D head;
	private AnimationPlayer avatarAnimation;
	private Vector3 targetPosition;
	private float targetYaw;
	private float targetPitch;

	public int PeerId { get; private set; } = NetworkManager.HostId;
	public bool IsLocalPlayer { get; private set; } = true;
	public bool IsFlashlightOn => flashlight != null && flashlight.IsVisibleInTree();
	public float HeadPitch => head.Rotation.X;
	public bool IsWalking { get; private set; }

	public void ConfigurePeer(int id, bool isLocal, bool online)
	{
		PeerId = id;
		IsLocalPlayer = isLocal;
		if (online)
			CollisionLayer = 0;
	}

	public override void _Ready()
	{
		head = GetNode<Node3D>("Head");
		flashlight = GetNode<SpotLight3D>("Head/Flashlight");
		Camera3D camera = GetNode<Camera3D>("Head/Camera3D");
		SetProcessUnhandledInput(IsLocalPlayer);
		if (IsLocalPlayer)
		{
			camera.MakeCurrent();
			Input.MouseMode = Input.MouseModeEnum.Captured;
			return;
		}

		camera.QueueFree();
		var animations = GetNodeOrNull<Node3D>("Avatar")?.FindChildren("*", "AnimationPlayer", owned: false);
		if (animations != null && animations.Count > 0)
		{
			avatarAnimation = (AnimationPlayer)animations[0];
			foreach (string clip in new[] { "Idle", "Walk" })
			{
				if (avatarAnimation.HasAnimation(clip))
					avatarAnimation.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
			}
		}
		ApplyPose(GlobalPosition, Rotation.Y, HeadPitch, IsFlashlightOn, false, snap: true);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false } key)
		{
			if (key.Keycode == Key.F11)
			{
				ToggleFullscreen();
				return;
			}
			if (key.Keycode == Key.F8)
			{
				GetViewport().SetInputAsHandled();
				GetNode<NetworkManager>("/root/NetworkManager").ReturnToTitle();
				return;
			}
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
			rotation.X = Mathf.Clamp(rotation.X, Mathf.DegToRad(-89), Mathf.DegToRad(89));
			head.Rotation = rotation;
		}

		if (@event.IsActionPressed("ui_cancel"))
			Input.MouseMode = Input.MouseModeEnum.Visible;

		if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
			Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!IsLocalPlayer)
		{
			float weight = 1f - Mathf.Exp(-22f * (float)delta);
			GlobalPosition = GlobalPosition.Lerp(targetPosition, weight);
			Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, targetYaw, weight), 0f);
			head.Rotation = new Vector3(Mathf.Lerp(HeadPitch, targetPitch, weight), 0f, 0f);
			return;
		}

		Vector3 velocity = Velocity;
		if (!IsOnFloor())
			velocity += GetGravity() * (float)delta;

		Vector2 input = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
		Vector3 direction = Transform.Basis * new Vector3(input.X, 0f, input.Y);
		velocity.X = direction.X * Speed;
		velocity.Z = direction.Z * Speed;
		Velocity = velocity;
		MoveAndSlide();
		Vector3 movement = GetRealVelocity();
		IsWalking = IsOnFloor() && new Vector2(movement.X, movement.Z).LengthSquared() > 0.04f;
	}

	public void ApplyPose(Vector3 position, float yaw, float pitch, bool beamOn, bool walking, bool snap = false)
	{
		targetPosition = position;
		targetYaw = yaw;
		targetPitch = pitch;
		flashlight.Visible = beamOn;
		IsWalking = walking;
		if (snap || GlobalPosition.DistanceSquaredTo(position) > 25f)
		{
			GlobalPosition = position;
			Rotation = new Vector3(0f, yaw, 0f);
			head.Rotation = new Vector3(pitch, 0f, 0f);
		}

		string clip = walking ? "Walk" : "Idle";
		if (avatarAnimation != null && avatarAnimation.CurrentAnimation != clip && avatarAnimation.HasAnimation(clip))
			avatarAnimation.Play(clip, 0.15);
	}

	private void ToggleFullscreen()
	{
		Window window = GetWindow();
		if (window.Mode == Window.ModeEnum.Fullscreen || window.Mode == Window.ModeEnum.ExclusiveFullscreen)
			window.Mode = Window.ModeEnum.Windowed;
		else
			window.Mode = Window.ModeEnum.Fullscreen;
	}
}
