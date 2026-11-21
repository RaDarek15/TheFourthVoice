using Godot;

public partial class TitleScreen : Control
{
	private const string WorldScene = "res://scenes/world.tscn";

	private Button startButton;
	private Label title;
	private Label hint;

	public override void _Ready()
	{
		title = GetNode<Label>("%Title");
		hint = GetNode<Label>("%Hint");
		startButton = GetNode<Button>("%Start");
		startButton.Pressed += StartGame;
		GetNode<Button>("%Quit").Pressed += () => GetTree().Quit();
		Resized += FitTitle;

		FitTitle();
		Input.MouseMode = Input.MouseModeEnum.Visible;
		startButton.GrabFocus();
	}

	private void FitTitle()
	{
		int fontSize = Mathf.RoundToInt(Mathf.Clamp(Size.X * 0.085f, 48f, 116f));
		title.AddThemeFontSizeOverride("font_size", fontSize);
	}

	private async void StartGame()
	{
		string caption = startButton.Text;
		startButton.Disabled = true;
		startButton.Text = "Loading…";

		SceneTree tree = GetTree();
		await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		Error result = tree.ChangeSceneToFile(WorldScene);
		if (result == Error.Ok)
			return;

		GD.PushError($"Could not open {WorldScene}: {result}");
		hint.Text = "Couldn't load the forest. Try again.";
		startButton.Text = caption;
		startButton.Disabled = false;
		startButton.GrabFocus();
	}
}
