using Godot;

public partial class Jumpscare : SubViewportContainer
{
	[Export] public PackedScene MimicModel;

	private const float LungeTime = 0.37f;
	private const float ImpactTime = 0.62f;
	private const float FadeTime = 0.84f;
	private const float EndTime = 1.14f;

	private SubViewport stage;
	private Node3D mimic;
	private AnimationPlayer animator;
	private Camera3D camera;
	private OmniLight3D faceLight;
	private ColorRect blackout;
	private AudioStreamPlayer rushStep;
	private AudioStreamPlayer sting;
	private Vector3 cameraRest;
	private float elapsed;

	public override void _Ready()
	{
		stage = GetNode<SubViewport>("Stage");
		camera = stage.GetNode<Camera3D>("Camera");
		cameraRest = camera.Position;
		faceLight = camera.GetNode<OmniLight3D>("FaceLight");
		blackout = GetNode<ColorRect>("Blackout");
		rushStep = GetNode<AudioStreamPlayer>("RushStep");
		sting = GetNode<AudioStreamPlayer>("Sting");
		SetProcess(false);
	}

	public void Play()
	{
		if (Visible)
			return;

		elapsed = 0f;
		mimic = MimicModel.Instantiate<Node3D>();
		stage.AddChild(mimic);
		var animations = mimic.FindChildren("*", "AnimationPlayer", owned: false);
		animator = animations.Count > 0 ? (AnimationPlayer)animations[0] : null;
		animator?.Play("Run", 0, 1.5f);
		animator?.Advance(0);
		sting.Stream ??= CreateSting();
		rushStep.PitchScale = 0.87f;
		rushStep.VolumeDb = -13f;
		rushStep.Play();
		UpdateShot();
		Show();
		stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		SetProcess(true);
	}

	public override void _Process(double delta)
	{
		float previous = elapsed;
		elapsed += (float)delta;
		if (previous < 0.19f && elapsed >= 0.19f)
		{
			rushStep.PitchScale = 0.78f;
			rushStep.VolumeDb = -8f;
			rushStep.Play();
		}
		if (previous < LungeTime && elapsed >= LungeTime)
			animator?.Play("Lunge", 0.04, 1.9f);
		if (previous < ImpactTime && elapsed >= ImpactTime)
			sting.Play();

		if (elapsed >= EndTime)
		{
			Hide();
			stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
			mimic.QueueFree();
			mimic = null;
			animator = null;
			SetProcess(false);
			return;
		}

		UpdateShot();
	}

	private void UpdateShot()
	{
		float rush = Mathf.Clamp(elapsed / LungeTime, 0f, 1f);
		float lunge = Mathf.SmoothStep(LungeTime, ImpactTime - 0.035f, elapsed);
		mimic.Position = new Vector3(
			Mathf.Lerp(-0.55f, 0.05f, rush),
			0f,
			Mathf.Lerp(-3.4f, 0.32f, rush * rush) + 0.5f * lunge);
		mimic.Rotation = Vector3.Up * Mathf.Lerp(0.19f, -0.06f, rush);

		float sinceImpact = elapsed - ImpactTime;
		float impact = sinceImpact >= 0f ? Mathf.Exp(-10f * sinceImpact) : 0f;
		float recoil = 0.16f * impact + 0.13f * Mathf.SmoothStep(0f, 0.12f, sinceImpact);
		float shake = Mathf.Lerp(0.012f, 0.035f, rush) + 0.11f * impact;
		camera.Position = cameraRest + new Vector3(
			Mathf.Sin(53f * elapsed) * shake,
			Mathf.Sin(39f * elapsed) * shake - 0.14f * impact,
			recoil);
		camera.Rotation = new Vector3(
			-0.035f + Mathf.Sin(46f * elapsed) * shake + 0.035f * impact,
			Mathf.Sin(57f * elapsed) * shake * 0.65f,
			-0.025f + Mathf.Sin(31f * elapsed) * shake - 0.13f * impact);
		camera.Fov = 66f - 9f * rush + 8f * impact;
		faceLight.LightEnergy = 0.65f + 0.75f * rush + 1.25f * impact;
		blackout.Color = new Color(0f, 0f, 0f, Mathf.SmoothStep(FadeTime, EndTime, elapsed));
	}

	private static AudioStreamWav CreateSting()
	{
		const int rate = 24000;
		const int frames = rate * 3 / 5;
		byte[] samples = new byte[frames * 2];
		using var random = new RandomNumberGenerator { Seed = 4372 };
		float bodyPhase = 0f;
		float raspPhase = 0f;
		float roughNoise = 0f;
		float lowNoise = 0f;
		for (int i = 0; i < frames; i++)
		{
			float time = (float)i / rate;
			float tail = 1f - Mathf.SmoothStep(0.39f, 0.6f, time);
			float attack = Mathf.Min(time * 270f, 1f);
			float bodyDecay = Mathf.Exp(-28f * time);
			bodyPhase += Mathf.Tau * (49f + 105f * bodyDecay) / rate;
			raspPhase += Mathf.Tau * (195f + 340f * Mathf.Exp(-7f * time) + 35f * Mathf.Sin(152f * time)) / rate;
			roughNoise = Mathf.Lerp(roughNoise, random.RandfRange(-1f, 1f), 0.57f);
			lowNoise = Mathf.Lerp(lowNoise, roughNoise, 0.09f);
			float thud = Mathf.Sin(bodyPhase) * 0.57f * Mathf.Exp(-13f * time);
			float rasp = Mathf.Sin(raspPhase) * 0.19f + Mathf.Sin(2.37f * raspPhase) * 0.14f +
				(roughNoise - lowNoise) * 0.45f;
			float mix = (thud + rasp * Mathf.Exp(-5.7f * time)) * attack * tail;
			short sample = (short)(Mathf.Clamp(mix, -1f, 1f) * short.MaxValue);
			samples[i * 2] = (byte)sample;
			samples[i * 2 + 1] = (byte)(sample >> 8);
		}
		return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = samples };
	}
}
