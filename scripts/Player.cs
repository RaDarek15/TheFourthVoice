using Godot;

public partial class Player : CharacterBody3D
{
	[Export] public float Speed = 5f;
	[Export] public float MouseSensitivity = 0.002f;

	[Export] public PackedScene PhoneModel;
	[Export] public PackedScene FlashlightModel;
	[Export] public StyleBox SelectedSlotStyle;

	private AnimationPlayer animator;
	private SpotLight3D flashlight;
	private Node3D head;
	private Node3D phoneItem;
	private Node3D flashlightItem;
	private StyleBox normalSlotStyle;
	private PanelContainer phoneSlot;
	private PanelContainer flashlightSlot;
	private RayCast3D interactionRay;
	private Label interactionHint;
	private bool flashlightPowered = true;
	private bool holdingFlashlight;
	private bool interactionRequested;
	private Vector3 targetPosition;
	private float targetYaw;
	private float targetPitch;

	public int PeerId { get; private set; } = NetworkManager.HostId;
	public bool IsLocalPlayer { get; private set; } = true;
	public bool IsFlashlightOn => flashlight != null && flashlight.IsVisibleInTree();
	public float HeadPitch => head.Rotation.X;
	public bool IsWalking { get; private set; }
	public bool IsRunning { get; private set; }
	public bool HoldingFlashlight => holdingFlashlight;

	public void ConfigurePeer(int id, bool isLocal, bool online)
	{
		PeerId = id;
		IsLocalPlayer = isLocal;
		if (online)
			CollisionLayer = 1 << 1;
	}

	public override void _Ready()
	{
		head = GetNode<Node3D>("Head");
		flashlight = GetNode<SpotLight3D>("Head/Flashlight");
		interactionRay = GetNode<RayCast3D>("Head/Camera3D/InteractionRay");
		interactionRay.AddException(this);
		interactionHint = GetNode<Label>("HUD/InteractionHint");
		phoneSlot = GetNode<PanelContainer>("HUD/Hotbar/Phone");
		flashlightSlot = GetNode<PanelContainer>("HUD/Hotbar/Flashlight");
		normalSlotStyle = phoneSlot.GetThemeStylebox("panel");

		animator = GetNode<AnimationPlayer>("Model/AnimationPlayer");
		foreach (string clip in new[] { "Idle", "Walk", "Run" })
			animator.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
		animator.Play("Idle");

		var skeleton = GetNode<Skeleton3D>("Model/Player/Skeleton3D");
		var grip = skeleton.GetNode<Node3D>("Hand_R/FlashlightSocket");
		skeleton.AddChild(new HeldItemPose { Head = head, GripToWrist = grip.Transform.AffineInverse() });

		phoneItem = PhoneModel.Instantiate<Node3D>();
		phoneItem.Position = new Vector3(0f, 0.05f, 0.015f);
		phoneItem.Rotation = new Vector3(-0.22f, Mathf.Pi, 0.18f);
		grip.AddChild(phoneItem);

		flashlightItem = FlashlightModel.Instantiate<Node3D>();
		flashlightItem.RotateY(Mathf.Pi);
		grip.AddChild(flashlightItem);
		Node3D lens = flashlightItem.GetNode<Node3D>("Flashlight/LightOrigin");
		flashlight.Reparent(lens, keepGlobalTransform: false);

		EquipFlashlight(true);
		Camera3D camera = GetNode<Camera3D>("Head/Camera3D");
		SetProcessUnhandledInput(IsLocalPlayer);
		if (IsLocalPlayer)
		{
			camera.MakeCurrent();
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
		else
		{
			GetNode<CanvasLayer>("HUD").Hide();
			camera.QueueFree();
			targetPosition = GlobalPosition;
			targetYaw = Rotation.Y;
			targetPitch = HeadPitch;
		}
	}

	private void EquipFlashlight(bool equip)
	{
		holdingFlashlight = equip;
		flashlightItem.Visible = equip;
		phoneItem.Visible = !equip;
		flashlight.Visible = equip && flashlightPowered;
		if (!IsLocalPlayer)
			return;

		PanelContainer selected = equip ? flashlightSlot : phoneSlot;
		PanelContainer stowed = equip ? phoneSlot : flashlightSlot;
		stowed.AddThemeStyleboxOverride("panel", normalSlotStyle);
		selected.AddThemeStyleboxOverride("panel", SelectedSlotStyle);
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
			if (holdingFlashlight)
				flashlightPowered = !flashlightPowered;
			else
				flashlightPowered = true;
			EquipFlashlight(true);
			return;
		}

		if (@event.IsActionPressed("inventory_phone"))
			EquipFlashlight(false);
		if (@event.IsActionPressed("inventory_flashlight"))
			EquipFlashlight(true);
		if (@event.IsActionPressed("interact"))
			interactionRequested = true;

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
		Vector2 input = Input.GetVector(
			"move_left",
			"move_right",
			"move_forward",
			"move_backward"
		);

		Vector3 velocity = Velocity;
		if (!IsOnFloor())
			velocity += GetGravity() * (float)delta;

		Vector3 direction = Transform.Basis * new Vector3(input.X, 0f, input.Y);
		bool sprinting = Input.IsActionPressed("sprint");
		float speed = sprinting ? Speed * 1.5f : Speed;
		velocity.X = direction.X * speed;
		velocity.Z = direction.Z * speed;

		Velocity = velocity;
		MoveAndSlide();

		Vector3 travel = GetRealVelocity();
		travel.Y = 0f;
		IsWalking = travel.LengthSquared() >= 0.04f;
		IsRunning = IsWalking && sprinting;
		UpdateAnimation();

		if (interactionRequested)
			interactionRay.ForceRaycastUpdate();
		HutDoor door = interactionRay.GetCollider() as HutDoor;
		interactionHint.Visible = door != null;
		if (door != null)
		{
			if (interactionRequested)
				GetNode<NetworkManager>("/root/NetworkManager").ToggleDoor(door);
			interactionHint.Text = door.InteractionHint;
		}
		interactionRequested = false;
	}

	public void ApplyPose(Vector3 position, float yaw, float pitch, bool equipped, bool beamOn, bool walking, bool running, bool snap = false)
	{
		targetPosition = position;
		targetYaw = yaw;
		targetPitch = pitch;
		if (equipped)
			flashlightPowered = beamOn;
		EquipFlashlight(equipped);
		IsWalking = walking;
		IsRunning = running;
		if (snap || GlobalPosition.DistanceSquaredTo(position) > 25f)
		{
			GlobalPosition = position;
			Rotation = new Vector3(0f, yaw, 0f);
			head.Rotation = new Vector3(pitch, 0f, 0f);
		}

		UpdateAnimation();
	}

	private void UpdateAnimation()
	{
		string clip = IsRunning ? "Run" : IsWalking ? "Walk" : "Idle";
		if (animator.CurrentAnimation != clip)
			animator.Play(clip, 0.16);
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
