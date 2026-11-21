using Godot;

public partial class Jumpscare : SubViewportContainer
{
	[Export] public PackedScene MimicModel;

	public void Play()
	{
		var stage = GetNode<SubViewport>("Stage");
		var camera = stage.GetNode<Camera3D>("Camera");
		var model = MimicModel.Instantiate<Node3D>();
		stage.AddChild(model);
		var animations = model.FindChildren("*", "AnimationPlayer", owned: false);
		if (animations.Count > 0 && animations[0] is AnimationPlayer animator && animator.HasAnimation("Lunge"))
			animator.Play("Lunge");

		Show();
		stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		var sting = GetNode<AudioStreamPlayer>("Sting");
		sting.Stream = CreateSting();
		sting.Play();

		Tween reveal = CreateTween();
		reveal.TweenProperty(camera, "position", new Vector3(-0.1f, 1.78f, 0.94f), 0.24)
			.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		reveal.TweenInterval(0.67);
		reveal.TweenCallback(Callable.From(() =>
		{
			Hide();
			stage.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
			model.QueueFree();
		}));
	}

	private static AudioStreamWav CreateSting()
	{
		const int rate = 22050;
		byte[] samples = new byte[rate * 2];
		var random = new RandomNumberGenerator { Seed = 4372 };
		float noise = 0f;
		float phase = 0f;
		for (int i = 0; i < rate; i++)
		{
			float time = (float)i / rate;
			noise = Mathf.Lerp(noise, random.RandfRange(-1f, 1f), 0.36f);
			float envelope = Mathf.Min(time * 180f, 1f) * Mathf.Exp(-5.8f * time) * (1f - time);
			phase += Mathf.Tau * Mathf.Lerp(490f, 86f, Mathf.Sqrt(time)) / rate;
			short sample = (short)((Mathf.Sin(phase) * 0.38f + Mathf.Sin(phase * 2.56f) * 0.22f + noise * 0.58f)
				* envelope * short.MaxValue);
			samples[i * 2] = (byte)sample;
			samples[i * 2 + 1] = (byte)(sample >> 8);
		}
		return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = samples };
	}
}
