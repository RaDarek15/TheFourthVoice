using Godot;

public partial class TitleScreen : Control
{
	private const string DefaultHint = "Your flashlight can give you away.";

	private Button startButton;
	private Button hostButton;
	private Button joinButton;
	private Control joinRow;
	private LineEdit address;
	private LineEdit nickname;
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
		nickname = GetNode<LineEdit>("%Nickname");
		joinRow = address.GetParent<Control>();
		network = GetNode<NetworkManager>("/root/NetworkManager");
		nickname.Text = network.LocalNickname;

		startButton.Pressed += StartGame;
		hostButton.Pressed += HostOrLeave;
		joinButton.Pressed += JoinGame;
		address.TextSubmitted += _ => JoinGame();
		nickname.TextSubmitted += _ => startButton.GrabFocus();
		GetNode<Button>("%Quit").Pressed += () => GetTree().Quit();
		network.MenuStatusChanged += ShowStatus;
		Resized += FitTitle;

		FitTitle();
		ShowStatus(network.MenuMessage, false);
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
		int fontSize = Mathf.RoundToInt(Mathf.Clamp(Mathf.Min(Size.X * 0.064f, Size.Y * 0.12f), 32f, 94f));
		title.AddThemeFontSizeOverride("font_size", fontSize);
	}

	private async void StartGame()
	{
		if (startButton.Disabled)
			return;

		if (network.IsHosting)
		{
			network.StartMatch();
			return;
		}

		if (!TryGetNickname("Player", out string name))
			return;

		ShowStatus("Loading the forest…", true);
		startButton.Text = "Loading…";
		SceneTree tree = GetTree();
		await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
		if (network.PlaySolo(name) != Error.Ok)
		{
			ShowStatus("Couldn't load the forest. Try again.", false);
			startButton.GrabFocus();
		}
	}

	private void HostOrLeave()
	{
		if (network.IsOnline)
		{
			network.LeaveLobby();
			startButton.GrabFocus();
			return;
		}

		if (!TryGetNickname("Host", out string name))
			return;

		Error result = network.CreateServer(name, NetworkManager.MaxPlayers);
		if (result != Error.Ok)
			ShowStatus($"Could not host on port {NetworkManager.Port} ({result}).", false);
	}

	private void JoinGame()
	{
		if (joinButton.Disabled)
			return;

		if (!TryGetNickname($"Guest {GD.Randi() % 900 + 100}", out string name))
			return;

		Error result = network.JoinServer(address.Text, name);
		if (result == Error.InvalidParameter)
		{
			ShowStatus("Enter the host PC’s IP address.", false);
			address.GrabFocus();
		}
		else if (result != Error.Ok)
			ShowStatus($"Could not start the connection ({result}).", false);
	}

	private bool TryGetNickname(string fallback, out string name)
	{
		name = nickname.Text.Trim();
		if (name.Length == 0)
			name = fallback;

		if (NetworkManager.IsValidNickname(name))
			return true;

		ShowStatus("Use up to 16 letters, numbers, spaces, hyphens, or underscores in your nickname.", false);
		nickname.GrabFocus();
		return false;
	}

	private void ShowStatus(string message, bool busy)
	{
		hint.Text = string.IsNullOrEmpty(message) ? DefaultHint : message;
		bool online = network.IsOnline;
		startButton.Visible = !online || network.IsHosting;
		startButton.Text = online ? "Start LAN game" : "Start game";
		startButton.Disabled = busy || (online && !network.CanStart);
		hostButton.Text = online ? "Leave lobby" : "Host LAN game";
		hostButton.Disabled = busy && (!online || network.GroupSize > 0);
		joinRow.Visible = !online;
		joinButton.Disabled = busy || online;
		address.Editable = !busy && !online;
		nickname.Editable = !busy && !online;

		if (GetViewport().GuiGetFocusOwner() == null)
		{
			if (online && !hostButton.Disabled)
				hostButton.GrabFocus();
			else if (!startButton.Disabled)
				startButton.GrabFocus();
		}
	}
}
