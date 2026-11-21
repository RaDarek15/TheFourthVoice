using Godot;
using System;

public partial class OpeningCutscene : Node3D
{
	[Export] public PackedScene CarModel;
	[Export] public PackedScene CastModel;
	[Export] public PackedScene SilhouetteModel;
	[Export] public PackedScene PhoneModel;

	private const float TitleTime = 45.5f;
	private const float WhistleTime = 47.7f;
	private const float CallFailureTime = 43.15f;
	private const float HandoffTime = 50f;
	private const float RevealTime = 1.1f;
	private static readonly string[] Trails = { "HillTrail", "HutTrail", "BridgeTrail" };
	private static readonly string[] OfflineCast = { "Ty", "Pasażer z przodu", "Pasażer z tyłu" };
	private static readonly float[] DepartureDistances = { 26f, 23f, 26f };
	private const float DriverStep = 9.82f;
	private const float PassengerStep = 10.82f;
	private const float RearPassengerStep = 11.78f;
	// Cut on each step down. The first visible pose is outside the sill and behind the open door.
	private static readonly (float At, Vector3 Position)[][] Exits =
	{
		new (float, Vector3)[]
		{
			(DriverStep, new Vector3(-1.56f, 0.12f, 0.12f)),
			(10.34f, new Vector3(-2.49f, 0f, 0.16f)),
			(11.64f, new Vector3(-2.58f, 0f, -1.32f))
		},
		new (float, Vector3)[]
		{
			(PassengerStep, new Vector3(1.56f, 0.12f, 0.17f)),
			(11.35f, new Vector3(2.49f, 0f, 0.22f)),
			(12.66f, new Vector3(2.31f, 0f, -2.72f))
		},
		new (float, Vector3)[]
		{
			(RearPassengerStep, new Vector3(-1.56f, 0.12f, 0.18f)),
			(12.23f, new Vector3(-2.15f, 0f, 0.31f))
		}
	};

	private readonly record struct CastMember(Node3D Body, AnimationPlayer Animation, SpotLight3D Torch);

	private readonly Curve3D[] routes = new Curve3D[3];
	private CastMember[] cast;

	private readonly Vector3[] roadsideMarks = new Vector3[3];
	private readonly SpotLight3D[] headlights = new SpotLight3D[2];
	private readonly StandardMaterial3D[] headlampMaterials = new StandardMaterial3D[2];

	private World world;
	private Camera3D camera;
	private Label caption;
	private Label clock;
	private Label title;
	private Label skip;
	private Label waiting;
	private OmniLight3D cabinLight;
	private ColorRect blackout;
	private AudioStreamPlayer ambience;
	private AudioStreamPlayer carAudio;
	private AudioStreamPlayer footstep;
	private AudioStreamPlayer callFailed;
	private AudioStreamPlayer3D whistle;
	private DirectionalLight3D moon;
	private Node3D worldCar;
	private Node3D car;
	private Node3D driverDoor;
	private Node3D passengerDoor;
	private Node3D driverEye;
	private OmniLight3D dashLight;
	private Node3D silhouette;
	private AnimationPlayer silhouetteAnimation;
	private Node3D sign;
	private Node3D phone;
	private Label3D phoneAction;
	private Label3D phoneStatus;
	private Transform3D parked;
	private Vector3 bridgeLamp;
	private int localSlot;
	private string[] names = OfflineCast;
	private long startTick = -1;
	private Transform3D localStart;
	private int lastShot = -1;
	private int lastStep = -1;
	private float time;
	private float skippingFrom = -1f;
	private float releasedAt = -1f;
	private bool playing;

	internal void Prepare(World activeWorld, Func<Vector3, Vector3> ground)
	{
		world = activeWorld;
		camera = GetNode<Camera3D>("Camera");
		caption = GetNode<Label>("Overlay/Caption");
		clock = GetNode<Label>("Overlay/Clock");
		title = GetNode<Label>("Overlay/Title");
		skip = GetNode<Label>("Overlay/Skip");
		waiting = GetNode<Label>("Overlay/Waiting");
		blackout = GetNode<ColorRect>("Overlay/Blackout");
		ambience = GetNode<AudioStreamPlayer>("Woods");
		var woods = (AudioStreamWav)ambience.Stream.Duplicate();
		woods.LoopEnd = (int)Math.Round(woods.GetLength() * woods.MixRate);
		woods.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
		ambience.Stream = woods;
		carAudio = GetNode<AudioStreamPlayer>("CarSound");
		footstep = GetNode<AudioStreamPlayer>("Footstep");
		callFailed = GetNode<AudioStreamPlayer>("CallFailed");
		whistle = GetNode<AudioStreamPlayer3D>("Whistle");
		moon = GetNode<DirectionalLight3D>("Moon");
		moon.Hide();
		GetViewport().SizeChanged += FitText;
		FitText();

		worldCar = world.GetNode<Node3D>("MapProps/Car");
		sign = world.GetNode<Node3D>("MapProps/RoadsideSign");
		bridgeLamp = world.GetNode<OmniLight3D>("MapProps/WoodenBridge/Lamp").GlobalPosition;
		parked = worldCar.GlobalTransform;
		for (int i = 0; i < routes.Length; i++)
		{
			Vector3 offset = i switch
			{
				0 => new Vector3(1.32f, 0f, -2.2f),
				1 => new Vector3(3.3f, 0f, 0.4f),
				_ => new Vector3(-1.5f, 0f, -2.4f)
			};
			roadsideMarks[i] = ground(parked.Origin + offset);
			var trail = world.GetNode<Path3D>($"TrailRoutes/{Trails[i]}");
			routes[i] = GroundRoute(trail, roadsideMarks[i], ground);
		}
	}

	public override void _ExitTree()
	{
		if (camera != null)
			GetViewport().SizeChanged -= FitText;
	}

	private static Curve3D GroundRoute(Path3D trail, Vector3 roadsideMark, Func<Vector3, Vector3> ground)
	{
		var route = new Curve3D();
		Vector3 entry = trail.ToGlobal(trail.Curve.SampleBaked(0f));
		int approachSteps = Mathf.CeilToInt(roadsideMark.DistanceTo(entry));
		for (int i = 0; i < approachSteps; i++)
			route.AddPoint(ground(roadsideMark.Lerp(entry, (float)i / approachSteps)));
		for (float distance = 0f; distance <= 42f; distance += 0.65f)
			route.AddPoint(ground(trail.ToGlobal(trail.Curve.SampleBaked(distance))));
		return route;
	}

	internal Transform3D GetStart(int slot)
	{
		Vector3 point = TrailPoint(slot, DepartureDistances[slot]);
		Vector3 along = TrailPoint(slot, DepartureDistances[slot] + 0.45f) - point;
		along.Y = 0f;
		return new Transform3D(Basis.LookingAt(along), point + Vector3.Up * 0.045f);
	}

	internal void Stage(Player player, string[] matchNames)
	{
		names = matchNames.Length == 0 ? OfflineCast : matchNames;
		for (int i = 0; i < routes.Length; i++)
		{
			if (GetStart(i).Origin.IsEqualApprox(player.GlobalPosition))
				localSlot = i;
		}

		localStart = GetStart(localSlot);
		worldCar.Hide();
		moon.Show();
		BuildScene();
		UpdateShot(0f);
		camera.MakeCurrent();
		SetOpacity(blackout, 1f);
		waiting.Show();
		world.UpdateVisibleTrees();
	}

	internal void Begin(long scheduledTick)
	{
		startTick = scheduledTick;
		waiting.Hide();
		float offset = startTick < 0 ? 0f : ((long)Time.GetTicksMsec() - startTick) / 1000f;
		carAudio.Play(Mathf.Clamp(offset, 0f, (float)carAudio.Stream.GetLength()));
		ambience.Play();
		playing = true;
	}

	private void BuildScene()
	{
		car = CarModel.Instantiate<Node3D>();
		AddChild(car);
		car.GlobalTransform = parked;
		driverDoor = (Node3D)car.FindChild("DriverDoor", owned: false);
		passengerDoor = (Node3D)car.FindChild("PassengerDoor", owned: false);
		driverEye = (Node3D)car.FindChild("DriverEye", owned: false);
		dashLight = new OmniLight3D
		{
			Position = new Vector3(-0.18f, 0.97f, -0.3f),
			LightColor = new Color("b6cb95"), LightEnergy = 0.36f, OmniRange = 1.8f, LightSpecular = 0f
		};
		car.AddChild(dashLight);
		cabinLight = new OmniLight3D
		{
			Position = new Vector3(0f, 1.32f, 0.05f), OmniRange = 1.65f,
			LightColor = new Color("e7b88a"), LightEnergy = 0f, LightSpecular = 0.15f
		};
		car.AddChild(cabinLight);
		var rearLight = new OmniLight3D
		{
			Position = new Vector3(0f, 0.65f, 2.1f),
			LightColor = new Color("855047"), LightEnergy = 0.42f, OmniRange = 3.5f
		};
		car.AddChild(rearLight);
		for (int i = 0; i < headlights.Length; i++)
		{
			string side = i == 0 ? "L" : "R";
			var lens = (MeshInstance3D)car.FindChild($"Headlight_{side}", owned: false);
			var material = (StandardMaterial3D)lens.GetActiveMaterial(0).Duplicate();
			material.EmissionEnabled = true;
			material.Emission = new Color("ffddab");
			lens.SetSurfaceOverrideMaterial(0, material);
			headlampMaterials[i] = material;
			var beam = new SpotLight3D
			{
				Position = new Vector3(i == 0 ? -0.56f : 0.56f, 0.61f, -2.03f),
				RotationDegrees = new Vector3(-4f, 0f, 0f),
				LightColor = new Color("ffe1b3"), SpotRange = 28f, SpotAngle = 30f,
				SpotAngleAttenuation = 1.25f, SpotAttenuation = 0.72f, LightEnergy = 5f
			};
			car.AddChild(beam);
			headlights[i] = beam;
		}

		cast = new CastMember[names.Length];
		for (int i = 0; i < cast.Length; i++)
		{
			var body = new Node3D { Name = $"Cast_{i}" };
			AddChild(body);
			var model = CastModel.Instantiate<Node3D>();
			model.RotateY(Mathf.Pi);
			body.AddChild(model);
			if (i != 0)
				TintWardrobe(model, i == 1 ? new Color("bb553c") : new Color("6385a0"));
			var animation = model.GetNode<AnimationPlayer>("AnimationPlayer");
			animation.Play("Idle");
			var torch = new SpotLight3D
			{
				Position = new Vector3(0.25f, 1.05f, -0.22f),
				RotationDegrees = new Vector3(-17f, 0f, 0f),
				LightColor = new Color(i == 1 ? "f3d6a5" : "b2d2ee"),
				LightEnergy = 1.15f, SpotRange = 8f, SpotAngle = 29f
			};
			body.AddChild(torch);
			cast[i] = new CastMember(body, animation, torch);
		}

		silhouette = SilhouetteModel.Instantiate<Node3D>();
		silhouette.Scale = Vector3.One * 1.33f;
		AddChild(silhouette);
		var silhouetteMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color("14181b"), Roughness = 0.92f, MetallicSpecular = 0.18f
		};
		foreach (Node node in silhouette.FindChildren("*", "MeshInstance3D", owned: false))
			((MeshInstance3D)node).MaterialOverride = silhouetteMaterial;
		silhouetteAnimation = (AnimationPlayer)silhouette.FindChildren("*", "AnimationPlayer", owned: false)[0];
		silhouetteAnimation.GetAnimation("Run").LoopMode = Animation.LoopModeEnum.Linear;
		silhouette.Rotation = Basis.LookingAt(parked.Basis.X, useModelFront: true).GetEuler();
		var signLight = new OmniLight3D { LightColor = new Color("f0d3a4"), LightEnergy = 0.70f, OmniRange = 3.1f };
		AddChild(signLight);
		signLight.GlobalPosition = sign.ToGlobal(new Vector3(-0.2f, 1.85f, 1.25f));
		BuildPhone();
	}

	private static void TintWardrobe(Node3D model, Color tint)
	{
		foreach (Node node in model.FindChildren("*", "MeshInstance3D", owned: false))
		{
			var mesh = (MeshInstance3D)node;
			for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
			{
				if (mesh.GetActiveMaterial(i) is not StandardMaterial3D original ||
					original.ResourceName is not ("Jacket" or "Hood lining" or "Petrol canvas" or "Reinforced canvas"))
					continue;

				var material = (StandardMaterial3D)original.Duplicate();
				material.AlbedoColor *= tint;
				mesh.SetSurfaceOverrideMaterial(i, material);
			}
		}
	}

	private void BuildPhone()
	{
		phone = PhoneModel.Instantiate<Node3D>();
		camera.AddChild(phone);
		phone.Hide();

		var screen = new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(0.0636f, 0.136f) },
			Position = new Vector3(0f, -0.00035f, 0.0044f),
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				AlbedoColor = new Color("17262b")
			}
		};
		phone.AddChild(screen);
		AddPhoneText("23:59", 0.053f, 25, new Color("b4c9c7"));
		string contact = names[localSlot == 0 ? 1 : 0];
		AddPhoneText(contact, 0.018f, Mathf.Min(42, 224 / contact.Length), new Color("edf4e9"));
		phoneAction = AddPhoneText("Połącz", -0.020f, 30, new Color("c3dec8"));
		phoneStatus = AddPhoneText("Brak zasięgu", -0.027f, 32, new Color("ffd6b8"));
		var phoneLight = new OmniLight3D { Position = new Vector3(0.04f, 0.07f, 0.16f), LightColor = new Color("b0d4ec"),
			LightEnergy = 0.45f, OmniRange = 0.4f };
		phone.AddChild(phoneLight);
	}

	private Label3D AddPhoneText(string text, float y, int size, Color color)
	{
		var label = new Label3D
		{
			Text = text, Position = new Vector3(0f, y, 0.0049f),
			FontSize = size, PixelSize = 0.00025f, OutlineSize = 0,
			Modulate = color, DoubleSided = false
		};
		phone.AddChild(label);
		return label;
	}

	public override void _Input(InputEvent input)
	{
		if (!playing || releasedAt >= 0f)
			return;
		if (input.IsActionPressed("ui_cancel"))
		{
			GetViewport().SetInputAsHandled();
			if (skippingFrom < 0f)
				skippingFrom = time;
		}
	}

	public override void _Process(double delta)
	{
		if (!playing)
			return;

		float before = time;
		time = startTick < 0 ? time + (float)delta : ((long)Time.GetTicksMsec() - startTick) / 1000f;
		if (releasedAt >= 0f)
		{
			SetOpacity(blackout, 1f - Ease(Progress(time, releasedAt, releasedAt + RevealTime)));
			if (time >= releasedAt + RevealTime)
			{
				world.FinishOpening();
				QueueFree();
				playing = false;
			}
			return;
		}
		if (skippingFrom >= 0f)
		{
			float fade = Ease(Progress(time, skippingFrom, skippingFrom + 0.2f));
			SetOpacity(blackout, Mathf.Max(blackout.Modulate.A, fade));
			if (fade >= 1f)
				Release();
			return;
		}

		UpdateShot(time);
		UpdateOverlay(time);
		if (before < CallFailureTime && time >= CallFailureTime)
			callFailed.Play();
		if (before < WhistleTime && time >= WhistleTime)
		{
			whistle.GlobalPosition = camera.GlobalPosition - camera.GlobalBasis.Z * 6f + camera.GlobalBasis.X * 3f;
			whistle.Play();
		}
		if (time >= HandoffTime)
			Release();
	}

	private void Release()
	{
		releasedAt = time;
		SetOpacity(blackout, 1f);
		caption.Hide();
		clock.Hide();
		title.Hide();
		skip.Hide();
		GetNode<Control>("Overlay/Matte").Hide();
		GetNode<Control>("Overlay/Vignette").Hide();
		ambience.Stop();
		carAudio.Stop();
		footstep.Stop();
		callFailed.Stop();
		whistle.Stop();
		Hide();
		worldCar.Show();
		world.ReleaseOpeningCamera();
	}

	private void UpdateShot(float t)
	{
		if (t < 13.1f || lastShot <= 1)
			UpdateCar(t);
		UpdateCast(t);
		int shot;
		if (t < 9.1f)
		{
			shot = 0;
			float jolt = Mathf.Sin(Mathf.Pi * Progress(t, 3.85f, 4.41f));
			float dash = Ease(Progress(t, 5.55f, 6.87f));
			Vector3 eye = driverEye.GlobalPosition + car.GlobalBasis *
				new Vector3(Mathf.Sin(t * 3.25f) * 0.008f, -jolt * 0.077f, -jolt * 0.16f);
			Vector3 look = car.ToGlobal(new Vector3(-0.20f, Mathf.Lerp(1.35f, -0.25f, dash), -5.3f));
			Frame(eye, look, 67f);
		}
		else if (t < 13f)
		{
			shot = 1;
			FrameExit(t);
		}
		else if (t < 16.2f)
		{
			shot = 2;
			Vector3 arrow = sign.ToGlobal(new Vector3(0.16f, 1.49f, 0.08f));
			float drift = Progress(t, 13f, 16.2f);
			Frame(arrow + new Vector3(-0.75f + 0.32f * drift, 0.04f, 2.5f - drift * 0.45f), arrow, 43f);
		}
		else if (t < 19.7f)
		{
			shot = 3;
			float drift = Progress(t, 16.2f, 19.7f);
			Frame(bridgeLamp + new Vector3(-3.9f + drift * 0.50f, -0.1f, 7.8f - drift * 0.9f),
				bridgeLamp + new Vector3(-1.15f, -0.46f, -1.6f), 48f);
		}
		else if (t < 23.1f)
		{
			shot = 4;
			Vector3 entry = TrailPoint(0, 4.7f);
			float drift = Progress(t, 19.7f, 23.1f);
			Frame(entry + new Vector3(-1.05f + 0.5f * drift, 0.98f, 1.7f - 0.70f * drift),
				TrailPoint(0, 17.0f) + Vector3.Up * 0.73f, 53f);
		}
		else if (t < 26.2f)
		{
			shot = 5;
			float drift = Progress(t, 23.1f, 26.2f);
			Frame(parked.Origin + new Vector3(4.8f, 1.77f, 3.25f - 0.70f * drift),
				parked.Origin + new Vector3(-0.1f, 0.95f, -0.63f), 58f);
		}
		else if (t < 35.5f)
		{
			int slot = t < 29.3f ? 1 : t < 32.4f && cast.Length == 3 ? 2 : 0;
			shot = 6 + slot;
			Vector3 forward = -cast[slot].Body.GlobalBasis.Z;
			Vector3 side = cast[slot].Body.GlobalBasis.X * (slot == 2 ? -1f : 1f);
			Vector3 mark = cast[slot].Body.GlobalPosition;
			Frame(mark - forward * 2.7f + side * 1.26f + Vector3.Up * 1.48f,
				mark + forward * 1.95f + Vector3.Up * 1.08f, 52f);
		}
		else
		{
			shot = 9;
			Transform3D start = localStart;
			float turn = 1.9f * (Ease(Progress(t, 35.5f, 37.05f)) - Ease(Progress(t, 38.15f, 40.1f)));
			float glance = Ease(Progress(t, 39.95f, 40.62f));
			Vector3 eye = start.Origin + Vector3.Up * 1.60f;
			Vector3 forward = -start.Basis.Z.Rotated(Vector3.Up, turn);
			Frame(eye + forward * 0.15f, eye + forward * 7f + Vector3.Down * (0.85f * glance), 67f);
		}

		bool takingPhone = t >= 39.95f && t < TitleTime + 0.55f;
		phone.Visible = takingPhone;
		if (takingPhone)
		{
			float raise = Ease(Progress(t, 39.95f, 40.62f));
			float lower = Ease(Progress(t, 45.3f, TitleTime + 0.45f));
			phone.Position = new Vector3(Mathf.Lerp(0.17f, 0.047f, raise), -0.25f + (raise - lower) * 0.172f, -0.262f);
			phone.Rotation = new Vector3(-0.10f + Mathf.Sin(t * 2.1f) * 0.025f, -0.09f, 0.14f - raise * 0.09f);
			phoneAction.Text = t < 41.7f ? "Połącz" : t < CallFailureTime ? "Łączenie…" : "";
			phoneStatus.Visible = t >= CallFailureTime;
		}
		if (shot != lastShot)
		{
			lastShot = shot;
			world.UpdateVisibleTrees();
		}
	}

	private void FrameExit(float t)
	{
		Vector3 eye;
		Vector3 target;
		float fov;
		if (t < DriverStep)
		{
			eye = new Vector3(-3.5f, 1.13f, -1.46f);
			target = new Vector3(-0.78f, 0.82f, -0.24f);
			fov = 46f;
		}
		else if (t < PassengerStep)
		{
			eye = new Vector3(-3.22f, 0.42f, 0.92f);
			target = new Vector3(-1.42f, 0.42f, 0.21f);
			fov = 52f;
		}
		else if (t < RearPassengerStep || (cast.Length == 3 && t < 12.48f))
		{
			bool rear = t >= RearPassengerStep;
			eye = rear ? new Vector3(-3.46f, 0.52f, 1.48f) : new Vector3(3.44f, 0.50f, 1.55f);
			target = rear ? new Vector3(-1.61f, 0.44f, 0.30f) : new Vector3(1.58f, 0.47f, 0.12f);
			fov = 56f;
		}
		else
		{
			eye = new Vector3(-4.0f, 2.0f, -5.8f);
			target = new Vector3(-0.38f, 0.84f, -0.64f);
			fov = 59f;
		}
		Frame(parked * eye, parked * target, fov);
	}

	private void UpdateCar(float t)
	{
		float drive = Mathf.Min(t, 3.85f) * 5.46f;
		float brake = Progress(t, 3.85f, 4.64f);
		float remaining = Mathf.Max(0f, 25.2f - drive - 4.18f * (1f - (1f - brake) * (1f - brake)));
		float bend = Ease(Progress(remaining, 15f, 25.2f));
		Vector3 approach = parked.Origin + parked.Basis.Z * remaining - parked.Basis.X * (5.1f * bend);
		float steering = 0.54f * Mathf.Sin(Mathf.Pi * Progress(remaining, 13f, 33f));
		car.GlobalTransform = new Transform3D(parked.Basis.Rotated(Vector3.Up, -steering), approach);
		float opening = Ease(Progress(t, 9.18f, 9.65f));
		float driverOpen = opening - Ease(Progress(t, cast.Length == 3 ? 12.24f : 11.55f, 12.80f));
		float passengerOpen = opening - Ease(Progress(t, 11.85f, 12.45f));
		driverDoor.Rotation = new Vector3(0f, -1.40f * driverOpen, 0f);
		passengerDoor.Rotation = new Vector3(0f, 1.43f * passengerOpen, 0f);
		cabinLight.LightEnergy = 0.36f * Mathf.Max(driverOpen, passengerOpen);

		float flicker = 0.48f * ClickPulse(t, 6.52f) + 0.33f * ClickPulse(t, 7.75f);
		float power = Mathf.Max(0.025f, 1f - 0.68f * Ease(Progress(t, 4.58f, 6.18f))
			- 0.295f * Ease(Progress(t, 7.8f, 8.55f)) - flicker);
		for (int i = 0; i < headlights.Length; i++)
		{
			headlights[i].LightEnergy = 4.3f * power;
			headlampMaterials[i].EmissionEnergyMultiplier = 2.5f * power;
		}
		dashLight.LightEnergy = 0.48f * power;
		silhouette.Visible = t >= 3.55f && t < 4.12f;
		if (silhouette.Visible)
		{
			if (!silhouetteAnimation.IsPlaying())
				silhouetteAnimation.Play("Run", customSpeed: 1.5f);
			silhouette.GlobalPosition = parked.Origin - parked.Basis.Z * 3.0f + parked.Basis.X *
				Mathf.Lerp(-3.2f, 3.15f, Progress(t, 3.55f, 4.12f));
		}
		else if (silhouetteAnimation.IsPlaying())
			silhouetteAnimation.Stop();
	}

	private void UpdateCast(float t)
	{
		for (int slot = 0; slot < cast.Length; slot++)
		{
			CastMember actor = cast[slot];
			actor.Body.Visible = t >= Exits[slot][0].At && t < 35.5f;
			if (!actor.Body.Visible)
				continue;

			actor.Torch.Visible = t >= 22.9f;
			float leave = Progress(t, 25.85f + slot * 0.13f, 35.5f);
			float distance = DepartureDistances[slot] * leave;
			float settledAt = slot == 1 ? 13.65f : 12.94f;
			bool exiting = t < settledAt;
			Vector3 mark = exiting ? ExitPosition(slot, t, settledAt) : TrailPoint(slot, distance);
			Vector3 look;
			if (exiting)
				look = ExitPosition(slot, t + 0.045f, settledAt);
			else if (leave > 0f)
				look = TrailPoint(slot, distance + 0.46f);
			else
				look = parked * new Vector3(1.1f, 0f, 1.36f);
			look.Y = mark.Y;
			actor.Body.GlobalPosition = mark;
			if (mark.DistanceSquaredTo(look) > 0.0001f)
				actor.Body.LookAt(look);

			string clip = exiting || leave > 0f ? "Walk" : "Idle";
			if (actor.Animation.CurrentAnimation != clip)
			{
				actor.Animation.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
				actor.Animation.Play(clip, 0.18f);
			}
		}
		if ((t > 9.8f && t < 11.9f) || (t > 26f && t < 35.5f))
		{
			int step = Mathf.FloorToInt(t / 0.47f);
			if (step != lastStep)
			{
				lastStep = step;
				footstep.PitchScale = step % 2 == 0 ? 0.95f : 1.06f;
				footstep.Play();
			}
		}
	}

	private Vector3 ExitPosition(int slot, float t, float settledAt)
	{
		var points = Exits[slot];
		var from = points[0];
		for (int i = 1; i < points.Length; i++)
		{
			var to = points[i];
			if (t <= to.At)
				return parked * from.Position.Lerp(to.Position, Progress(t, from.At, to.At));
			from = to;
		}
		return (parked * from.Position).Lerp(roadsideMarks[slot], Progress(t, from.At, settledAt));
	}

	private void FitText()
	{
		float width = GetViewport().GetVisibleRect().Size.X;
		title.AddThemeFontSizeOverride("font_size", (int)Mathf.Clamp(width * 0.102f, 40f, 136f));
		caption.AddThemeFontSizeOverride("font_size", (int)Mathf.Clamp(width * 0.021f, 17f, 30f));
	}

	private void UpdateOverlay(float t)
	{
		int bridgeSpeaker = names.Length == 3 ? 2 : 1;
		string line = t switch
		{
			>= 4.55f and < 6.05f => $"{names[1]}: Co to było?",
			>= 8.03f and < 9.65f => names.Length == 2
				? $"{names[0]}: Nie odpala. Wysiadamy."
				: $"{names[0]}: Nie odpala. Wysiadajcie.",
			>= 13.05f and < 15.92f => $"{names[1]}: Jest drogowskaz. Sprawdzę leśniczówkę.",
			>= 16.30f and < 19.52f => $"{names[bridgeSpeaker]}: Przy mostku pali się światło. Może ktoś tam jest.",
			>= 19.78f and < 22.87f => $"{names[0]}: Wejdę wyżej. Może stamtąd uda się zadzwonić.",
			>= 23.38f and < 26.1f => $"{names[1]}: Jak nic nie znajdziemy, wracamy do auta.",
			>= 36.75f and < 39.0f => names.Length == 2
				? $"— {names[1 - localSlot]}?"
				: $"— {names[(localSlot + 1) % 3]}? {names[(localSlot + 2) % 3]}?",
			_ => ""
		};
		caption.Text = line;
		SetOpacity(clock, Ease(Progress(t, 0.65f, 1.45f)) * (1f - Ease(Progress(t, 2.88f, 3.54f))));
		SetOpacity(blackout, Mathf.Max(1f - Ease(Progress(t, 0f, 1.1f)), Ease(Progress(t, TitleTime - 0.18f, 46.32f))));
		SetOpacity(title, Ease(Progress(t, 45.87f, 46.93f)) * (1f - Ease(Progress(t, 49.27f, HandoffTime))));
		SetOpacity(skip, 0.72f * Ease(Progress(t, 1.25f, 2f)) * (1f - Ease(Progress(t, 44.7f, TitleTime))));
		ambience.VolumeDb = t < 9.1f ? -22f : -13f - 8f * Ease(Progress(t, 44.5f, 46.5f));
	}

	private Vector3 TrailPoint(int slot, float distance) => routes[slot].SampleBaked(distance);

	private void Frame(Vector3 position, Vector3 target, float fov)
	{
		camera.GlobalPosition = position;
		camera.LookAt(target);
		camera.Fov = fov;
	}

	private static float Progress(float time, float from, float to) => Mathf.Clamp((time - from) / (to - from), 0f, 1f);
	private static float Ease(float x) => x * x * (3f - 2f * x);
	private static float ClickPulse(float t, float at) => 1f - Mathf.Clamp(Mathf.Abs(t - at) / 0.16f, 0f, 1f);
	private static void SetOpacity(CanvasItem item, float opacity) => item.Modulate = new Color(1f, 1f, 1f, opacity);
}
