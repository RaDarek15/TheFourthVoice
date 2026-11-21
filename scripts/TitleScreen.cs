using Godot;

public partial class TitleScreen : Control
{
	private Button startButton;
	private Button soloButton;
	private Button hostButton;
	private Button joinButton;
	private Button leaveButton;
	private LineEdit address;
	private LineEdit nickname;
	private OptionButton groupSize;
	private Label title;
	private Label hint;
	private NetworkManager network;
	private bool busy;

	public override void _Ready()
	{
		title = GetNode<Label>("%Title");
		hint = GetNode<Label>("%Hint");
		startButton = GetNode<Button>("%Start");
		soloButton = GetNode<Button>("%Solo");
		hostButton = GetNode<Button>("%Host");
		joinButton = GetNode<Button>("%Join");
		leaveButton = GetNode<Button>("%Leave");
		address = GetNode<LineEdit>("%HostAddress");
		nickname = GetNode<LineEdit>("%Nickname");
		groupSize = GetNode<OptionButton>("%GroupSize");
		network = GetNode<NetworkManager>("/root/NetworkManager");

		groupSize.AddItem("2 graczy", 2);
		groupSize.AddItem("3 graczy", 3);
		groupSize.Select(1);
		nickname.Text = network.LocalNickname;
		nickname.TextChanged += _ => RefreshActions();
		address.TextChanged += _ => RefreshActions();
		address.TextSubmitted += _ => JoinGame();
		soloButton.Pressed += PlaySolo;
		startButton.Pressed += network.StartMatch;
		hostButton.Pressed += HostGame;
		joinButton.Pressed += JoinGame;
		leaveButton.Pressed += network.LeaveLobby;
		GetNode<Button>("%Quit").Pressed += () => GetTree().Quit();
		network.MenuStatusChanged += ShowStatus;
		Resized += FitTitle;

		FitTitle();
		ShowStatus(network.MenuMessage, false);
		Input.MouseMode = Input.MouseModeEnum.Visible;
		nickname.GrabFocus();
	}

	public override void _ExitTree()
	{
		if (IsInstanceValid(network))
			network.MenuStatusChanged -= ShowStatus;
	}

	private void FitTitle()
	{
		int fontSize = Mathf.RoundToInt(Mathf.Clamp(Mathf.Min(Size.X * 0.085f, Size.Y * 0.14f), 42f, 116f));
		title.AddThemeFontSizeOverride("font_size", fontSize);
	}

	private void PlaySolo()
	{
		if (soloButton.Disabled)
			return;
		ShowStatus("Wczytywanie lasu…", true);
		if (network.PlaySolo(nickname.Text) != Error.Ok)
			ShowStatus("Nie udało się wczytać lasu.", false);
	}

	private void HostGame()
	{
		if (hostButton.Disabled)
			return;
		Error result = network.CreateServer(nickname.Text, groupSize.GetSelectedId());
		if (result != Error.Ok)
			ShowStatus($"Nie udało się otworzyć lobby (port {NetworkManager.Port}).", false);
	}

	private void JoinGame()
	{
		if (joinButton.Disabled)
			return;

		Error result = network.JoinServer(address.Text, nickname.Text);
		if (result == Error.InvalidParameter)
		{
			ShowStatus("Podaj adres IP hosta.", false);
			address.GrabFocus();
		}
		else if (result != Error.Ok)
			ShowStatus("Nie udało się rozpocząć połączenia.", false);
	}

	private void ShowStatus(string message, bool isBusy)
	{
		hint.Text = string.IsNullOrEmpty(message) ? "Podaj nick. Możesz zagrać solo lub utworzyć lobby LAN." : message;
		busy = isBusy;
		RefreshActions();
	}

	private void RefreshActions()
	{
		bool online = network.IsOnline;
		bool canEnter = !busy && !online && NetworkManager.IsValidNickname(nickname.Text);
		nickname.Editable = !busy && !online;
		address.Editable = !busy && !online;
		groupSize.Disabled = busy || online;
		soloButton.Disabled = !canEnter;
		hostButton.Disabled = !canEnter;
		joinButton.Disabled = !canEnter || string.IsNullOrWhiteSpace(address.Text);
		startButton.Disabled = busy || !network.CanStart;
		leaveButton.Visible = online;
		leaveButton.Disabled = busy && network.GroupSize > 0;
		startButton.Visible = network.IsHosting;
		soloButton.Visible = !online;
		hostButton.GetParent<Control>().Visible = !online;
		address.GetParent<Control>().Visible = !online;
	}
}
