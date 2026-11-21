using Godot;

public partial class TitleScreen : Control
{
	private const string WorldScene = "res://scenes/world.tscn";

	private Button startButton;
	private Button hostButton;
	private Button joinButton;
	private LineEdit address;
	private Label title;
	private Label hint;
	private NetworkManager network;

	public override void _Ready()
	{
		title = GetNode<Label>("%Title");
		hint = GetNode<Label>("%Hint");
		startButton = GetNode<Button>("%Start");
		hostButton = GetNode<Button>("%Host");
		joinButton = GetNode<Button>("%Join");
		address = GetNode<LineEdit>("%HostAddress");
		network = GetNode<NetworkManager>("/root/NetworkManager");
		startButton.Pressed += StartGame;
		hostButton.Pressed += HostGame;
		joinButton.Pressed += JoinGame;
		address.TextSubmitted += _ => JoinGame();
		GetNode<Button>("%Quit").Pressed += () => GetTree().Quit();
		network.MenuStatusChanged += ShowStatus;
		if (!string.IsNullOrEmpty(network.MenuMessage))
			ShowStatus(network.MenuMessage, false);
		Resized += FitTitle;

		FitTitle();
		Input.MouseMode = Input.MouseModeEnum.Visible;
		startButton.GrabFocus();
	}
	public void showTitle()
	{
		FitTitle();
		Input.MouseMode = Input.MouseModeEnum.Visible;
		startButton.GrabFocus();
	}

	public override void _ExitTree()
	{
		if (IsInstanceValid(network))
			network.MenuStatusChanged -= ShowStatus;
	}

	private void FitTitle()
	{
		int fontSize = Mathf.RoundToInt(Mathf.Clamp(Size.X * 0.085f, 48f, 116f));
		title.AddThemeFontSizeOverride("font_size", fontSize);
	}

	private async void StartGame()
	{
		string caption = startButton.Text;
		SetBusy(true);
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
		SetBusy(false);
		startButton.GrabFocus();
	}

	private void HostGame()
	{
		ShowStatus("Opening the forest on UDP port 7777…", true);
		Error result = network.CreateServer();
		if (result != Error.Ok)
			ShowStatus($"Could not host on port 7777 ({result}).", false);
	}

	private void JoinGame()
	{
		if (joinButton.Disabled)
			return;

		Error result = network.JoinServer(address.Text);
		if (result == Error.InvalidParameter)
		{
			ShowStatus("Enter the host PC’s IP address.", false);
			address.GrabFocus();
		}
		else if (result != Error.Ok)
			ShowStatus($"Could not start the connection ({result}).", false);
	}

	private void ShowStatus(string message, bool busy)
	{
		hint.Text = message;
		SetBusy(busy);
	}

	private void SetBusy(bool busy)
	{
		startButton.Disabled = busy;
		hostButton.Disabled = busy;
		joinButton.Disabled = busy;
		address.Editable = !busy;
	}
}
