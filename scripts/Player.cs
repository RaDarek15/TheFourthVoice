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
	private Camera3D camera;
	private Vector3 cameraRestPosition;
	private Node3D phoneItem;
	private Node3D flashlightItem;
	private StyleBox normalSlotStyle;
	private PanelContainer phoneSlot;
	private PanelContainer flashlightSlot;
	private RayCast3D interactionRay;
	private Label interactionHint;
	private AudioStreamPlayer3D footsteps;
	private AudioStreamPlayer3D whistle;
	private ulong nextWhistleTime;
	private Control gameMenu;
	private Button resumeButton;
	private World world;
	private EscapeHud escapeHud;
	private Control hotbar;
	private Label starterSlot;
	private Label3D nameplate;
	private bool repairHeld;
	private uint footCollisionLayer;
	private uint footCollisionMask;
	private EscapeOutcome escapeResult;
	private bool flashlightPowered = true;
	private bool holdingFlashlight;
	private bool interactionRequested;
	private Vector3 targetPosition;
	private float targetYaw;
	private float targetPitch;

	public int PeerId { get; private set; } = NetworkManager.HostId;
	public string Nickname { get; private set; } = "";
	public bool IsLocalPlayer { get; private set; } = true;
	public bool IsFlashlightOn => flashlight != null && flashlight.IsVisibleInTree();
	public float HeadPitch => head.Rotation.X;
	public bool IsWalking { get; private set; }
	public bool IsRunning { get; private set; }
	public bool HoldingFlashlight => holdingFlashlight;
	public SurvivorState State { get; private set; }
	public int SeatIndex { get; private set; } = -1;
	public float ViewHeight => head.Position.Y;

	public void ConfigurePeer(int id, bool isLocal, bool online, string nickname)
	{
		PeerId = id;
		IsLocalPlayer = isLocal;
		Nickname = nickname;
		if (online)
			CollisionLayer = 1 << 1;
	}

	public override void _Ready()
	{
		world = GetParent() as World ?? GetParent().GetParent() as World;
		footCollisionLayer = CollisionLayer;
		footCollisionMask = CollisionMask;
		footsteps = GetNode<AudioStreamPlayer3D>("Footsteps");
		if (footsteps.Stream is AudioStreamMP3 walkingClip)
			walkingClip.Loop = true;
		whistle = GetNode<AudioStreamPlayer3D>("Whistle");
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
		if (IsLocalPlayer)
			FirstPersonBody.KeepLimbs(skeleton);
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
		camera = GetNode<Camera3D>("Head/Camera3D");
		cameraRestPosition = camera.Position;
		SetProcessUnhandledInput(IsLocalPlayer);
		if (IsLocalPlayer)
		{
			escapeHud = GetNode<EscapeHud>("HUD/EscapeHUD");
			hotbar = GetNode<Control>("HUD/Hotbar");
			starterSlot = hotbar.GetNode<Label>("JumpStarterItem");
			escapeHud.ExitButton.Pressed += () => world?.Escape?.Interact(this, EscapeAction.Exit);
			escapeHud.DepartButton.Pressed += () => world?.Escape?.Interact(this, EscapeAction.Depart);
			escapeHud.MainMenuButton.Pressed += ReturnToMainMenu;
			gameMenu = GetNode<Control>("HUD/GameMenu");
			resumeButton = gameMenu.GetNode<Button>("Center/Panel/Actions/Resume");
			resumeButton.Pressed += () => SetGameMenuOpen(false);
			gameMenu.GetNode<Button>("Center/Panel/Actions/MainMenu").Pressed += ReturnToMainMenu;
			camera.MakeCurrent();
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
		else
		{
			GetNode<CanvasLayer>("HUD").Hide();
			nameplate = new Label3D
			{
				Text = Nickname, Position = Vector3.Up * 2.05f,
				Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 30, PixelSize = 0.0028f,
				OutlineSize = 6, VisibilityRangeEnd = 8f
			};
			AddChild(nameplate);
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

	internal void SetOpeningLocked(bool locked)
	{
		SetPhysicsProcess(!locked);
		SetProcessUnhandledInput(!locked);
		GetNode<CanvasLayer>("HUD").Visible = !locked;
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
				ReturnToMainMenu();
				return;
			}
		}

		if (State != SurvivorState.OnFoot || escapeResult != EscapeOutcome.InProgress)
			return;

		if (@event.IsActionPressed("ui_cancel") ||
			(!gameMenu.Visible && InputMap.HasAction("toggle_main_menu") && @event.IsActionPressed("toggle_main_menu")))
		{
			GetViewport().SetInputAsHandled();
			SetGameMenuOpen(!gameMenu.Visible);
			return;
		}

		if (gameMenu.Visible)
			return;

		if (@event.IsActionPressed("whistle"))
		{
			GetViewport().SetInputAsHandled();
			GetNode<NetworkManager>("/root/NetworkManager").Whistle(this);
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
			rotation.X = Mathf.Clamp(rotation.X, Mathf.DegToRad(-89), Mathf.DegToRad(89));
			head.Rotation = rotation;
			// Lean forward when looking down so the camera stays in front of the waistband.
			float lean = Mathf.SmoothStep(Mathf.DegToRad(50f), Mathf.DegToRad(84f), -rotation.X);
			camera.Position = cameraRestPosition + Vector3.Up * (0.44f * lean);
		}

		if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
			Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	public override void _PhysicsProcess(double delta)
	{
		EscapeObjective escape = world?.Escape;
		if (IsLocalPlayer)
		{
			escapeHud.Update(this, escape, gameMenu.Visible, escapeResult);
			starterSlot.Visible = escape?.CarrierId == PeerId;
		}
		if (State != SurvivorState.OnFoot || escapeResult != EscapeOutcome.InProgress)
			return;

		if (!IsLocalPlayer)
		{
			float weight = 1f - Mathf.Exp(-22f * (float)delta);
			GlobalPosition = GlobalPosition.Lerp(targetPosition, weight);
			Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, targetYaw, weight), 0f);
			head.Rotation = new Vector3(Mathf.Lerp(HeadPitch, targetPitch, weight), 0f, 0f);
			return;
		}
		Vector2 input = gameMenu.Visible || repairHeld ? Vector2.Zero : Input.GetVector(
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
		IsWalking = IsOnFloor() && travel.LengthSquared() >= 0.04f;
		IsRunning = IsWalking && sprinting;
		UpdateAnimation();
		UpdateFootsteps();

		if (interactionRequested)
			interactionRay.ForceRaycastUpdate();
		GodotObject target = gameMenu.Visible ? null : interactionRay.GetCollider();
		HutDoor door = target as HutDoor;
		EscapeAction? action = escape?.ActionFor(this, target);
		if (repairHeld && (!Input.IsActionPressed("interact") ||
			(action != EscapeAction.StartRepair && escape?.RepairerId != PeerId)))
			StopRepair();

		string hint = escape?.HintFor(this, target) ?? "";
		if (door != null)
		{
			if (interactionRequested)
				GetNode<NetworkManager>("/root/NetworkManager").ToggleDoor(door);
			hint = door.InteractionHint;
		}
		else if (interactionRequested && action is EscapeAction use)
		{
			repairHeld = use == EscapeAction.StartRepair;
			escape.Interact(this, use);
		}
		if (escape?.RepairerId == PeerId && !gameMenu.Visible)
			hint = $"Repairing · {Mathf.CeilToInt(EscapeObjective.RepairSeconds - escape.RepairTime)} s";
		interactionHint.Text = hint;
		interactionHint.Visible = hint.Length != 0;
		interactionRequested = false;
	}

	private void StopRepair()
	{
		if (!repairHeld)
			return;
		repairHeld = false;
		world?.Escape?.Interact(this, EscapeAction.CancelRepair);
	}

	public void SetSurvivorState(SurvivorState state, int seat, Vector3 position, float yaw)
	{
		if (State == state)
			return;
		State = state;
		SeatIndex = seat;
		repairHeld = false;
		interactionRequested = false;
		Velocity = Vector3.Zero;
		targetPosition = GlobalPosition = position;
		targetYaw = yaw;
		targetPitch = 0f;
		Rotation = new Vector3(0f, yaw, 0f);
		head.Rotation = Vector3.Zero;
		bool onFoot = state == SurvivorState.OnFoot;
		GetNode<Node3D>("Model").Visible = onFoot;
		GetNode<OmniLight3D>("Head/OmniLight3D").Visible = onFoot;
		CollisionLayer = onFoot ? footCollisionLayer : 0;
		CollisionMask = onFoot ? footCollisionMask : 0;
		IsWalking = IsRunning = false;
		UpdateAnimation();
		UpdateFootsteps();
		if (!IsLocalPlayer)
		{
			nameplate.Visible = onFoot;
			return;
		}

		gameMenu.Hide();
		interactionHint.Hide();
		camera.Position = cameraRestPosition;
		hotbar.Visible = onFoot;
		GetNode<Control>("HUD/Reticle").Visible = onFoot;
		Input.MouseMode = onFoot ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
		if (state == SurvivorState.Dead)
			escapeHud.Scare();
		escapeHud.Update(this, world?.Escape, false, escapeResult);
	}

	public void ShowEscapeResult(EscapeOutcome outcome)
	{
		escapeResult = outcome;
		gameMenu.Hide();
	}

	private void SetGameMenuOpen(bool open)
	{
		gameMenu.Visible = open;
		interactionRequested = false;
		if (open)
			StopRepair();
		Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
		if (open)
			resumeButton.GrabFocus();
	}

	private void ReturnToMainMenu() => GetNode<NetworkManager>("/root/NetworkManager").ReturnToTitle();

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
		UpdateFootsteps();
	}

	public bool TryStartWhistle()
	{
		ulong now = Time.GetTicksMsec();
		if (now < nextWhistleTime)
			return false;

		nextWhistleTime = now + (ulong)(System.Math.Max(1.5, whistle.Stream.GetLength()) * 1000);
		return true;
	}

	public void PlayWhistle(Vector3 origin)
	{
		whistle.GlobalPosition = origin + Vector3.Up * 1.55f;
		whistle.Play();
	}

	private void UpdateFootsteps()
	{
		if (!IsWalking)
		{
			if (footsteps.Playing)
				footsteps.Stop();
			return;
		}

		footsteps.PitchScale = IsRunning ? 1.3f : 1f;
		if (!footsteps.Playing)
			footsteps.Play();
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
