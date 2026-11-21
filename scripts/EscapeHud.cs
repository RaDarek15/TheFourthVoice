using Godot;

public partial class EscapeHud : Control
{
	private Label objective;
	private ProgressBar repair;
	private Control vehicle;
	private Button depart;
	private Label vehicleStatus;
	private Control result;
	private Label resultTitle;
	private Label resultStatus;
	private Jumpscare jumpscare;

	public Button ExitButton { get; private set; }
	public Button DepartButton => depart;
	public Button MainMenuButton { get; private set; }

	public override void _Ready()
	{
		objective = GetNode<Label>("Objective");
		repair = GetNode<ProgressBar>("Repair");
		repair.MaxValue = EscapeObjective.RepairSeconds;
		vehicle = GetNode<Control>("Vehicle");
		depart = GetNode<Button>("Vehicle/Actions/Depart");
		ExitButton = GetNode<Button>("Vehicle/Actions/Exit");
		vehicleStatus = GetNode<Label>("Vehicle/Actions/Status");
		result = GetNode<Control>("Result");
		resultTitle = GetNode<Label>("Result/Center/Actions/Title");
		resultStatus = GetNode<Label>("Result/Center/Actions/Status");
		MainMenuButton = GetNode<Button>("Result/Center/Actions/MainMenu");
		jumpscare = GetNode<Jumpscare>("Jumpscare");
	}

	public void Scare() => jumpscare.Play();

	public void Update(Player player, EscapeObjective escape, bool menuOpen, EscapeOutcome outcome)
	{
		bool active = escape != null && escape.IsAvailable && !menuOpen;
		objective.Text = active && player.State == SurvivorState.OnFoot ? escape.ObjectiveFor(player) : "";
		repair.Visible = active && escape.RepairerId == player.PeerId;
		repair.Value = repair.Visible ? escape.RepairTime : 0;
		vehicle.Visible = active && player.State == SurvivorState.InCar && escape.Outcome == EscapeOutcome.InProgress;
		bool ready = vehicle.Visible && escape.CanDepart;
		depart.Disabled = !ready;
		vehicleStatus.Text = ready ? "All survivors are in the car" : "Waiting for surviving players";

		bool showingResult = !jumpscare.Visible && (player.State == SurvivorState.Dead || outcome != EscapeOutcome.InProgress);
		if (showingResult && !result.Visible)
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
			result.Show();
			MainMenuButton.GrabFocus();
		}
		result.Visible = showingResult;
		if (!showingResult)
			return;

		resultTitle.Text = player.State == SurvivorState.Dead ? "The Mimic caught you" : "You escaped";
		resultStatus.Text = outcome switch
		{
			EscapeOutcome.Escaped when player.State == SurvivorState.Dead => "The other survivors made it out.",
			EscapeOutcome.Escaped => "The car is back on the road.",
			EscapeOutcome.Lost => "No one made it out.",
			_ => "The other players can still escape."
		};
	}
}
