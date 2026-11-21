
using Godot;

public partial class Player : CharacterBody3D
{
	[Export] public float Speed = 5.0f;
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

	public bool IsFlashlightOn => flashlight != null && flashlight.IsVisibleInTree();

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
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void EquipFlashlight(bool equip)
	{
		holdingFlashlight = equip;
		flashlightItem.Visible = equip;
		phoneItem.Visible = !equip;
		flashlight.Visible = equip && flashlightPowered;
		PanelContainer selected = equip ? flashlightSlot : phoneSlot;
		PanelContainer stowed = equip ? phoneSlot : flashlightSlot;
		stowed.AddThemeStyleboxOverride("panel", normalSlotStyle);
		selected.AddThemeStyleboxOverride("panel", SelectedSlotStyle);
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

		bool sprinting = Input.IsActionPressed("sprint");
		float speed = sprinting ? Speed * 1.5f : Speed;
		velocity.X = direction.X * speed;
		velocity.Z = direction.Z * speed;

		Velocity = velocity;

		MoveAndSlide();

		Vector3 travel = GetRealVelocity();
		travel.Y = 0f;
		string clip = travel.LengthSquared() < 0.04f ? "Idle" : sprinting ? "Run" : "Walk";
		if (animator.CurrentAnimation != clip)
			animator.Play(clip, 0.16);

		if (interactionRequested)
			interactionRay.ForceRaycastUpdate();
		HutDoor door = interactionRay.GetCollider() as HutDoor;
		interactionHint.Visible = door != null;
		if (door != null)
		{
			if (interactionRequested)
				door.Toggle();
			interactionHint.Text = door.InteractionHint;
		}
		interactionRequested = false;
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
