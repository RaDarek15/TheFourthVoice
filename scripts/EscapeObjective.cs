using Godot;
using System.Linq;

public enum EscapeAction { PickUp, StartRepair, CancelRepair, Board, Exit, Depart }
public enum SurvivorState { OnFoot, InCar, Dead }
public enum EscapeOutcome { InProgress, Escaped, Lost }

public partial class EscapeObjective : Node
{
	public const float RepairSeconds = 20f;
	private const float DepartureSeconds = 2.4f;

	private static readonly Vector3[] SeatEyes =
	{
		new(-0.39f, 1.177f, 0.23f), new(0.39f, 1.177f, 0.23f), new(0, 1.17f, 1.12f)
	};

	private World world;
	private NetworkManager network;
	private StaticBody3D starter;
	private Node3D car;
	private Vector3 parked;
	private Vector3 departureDirection;
	private AudioStreamPlayer3D engine;
	private float drivingTime;
	private bool resultShown;

	public bool IsAvailable => car != null && starter != null;
	public int CarrierId { get; private set; }
	public int RepairerId { get; private set; }
	public float RepairTime { get; private set; }
	public bool Repaired { get; private set; }
	public EscapeOutcome Outcome { get; private set; }
	public Vector3 StarterPosition => starter.GlobalPosition;
	public bool CanDepart => Repaired && Outcome == EscapeOutcome.InProgress &&
		world.Players.Any(p => p.State == SurvivorState.InCar) &&
		world.Players.All(p => p.State != SurvivorState.OnFoot);

	public override void _Ready()
	{
		world = GetParent<World>();
		network = GetNode<NetworkManager>("/root/NetworkManager");
		car = world.GetNodeOrNull<Node3D>("MapProps/Car");
		starter = world.GetNodeOrNull<StaticBody3D>("MapProps/ForesterHut/Workbench/JumpStarter");
		if (!IsAvailable)
			return;

		parked = car.GlobalPosition;
		departureDirection = -car.GlobalBasis.Z.Normalized();
		engine = new AudioStreamPlayer3D
		{
			Stream = GD.Load<AudioStream>("res://assets/audio/intro/car.wav"),
			UnitSize = 6f,
			VolumeDb = -10f
		};
		car.AddChild(engine);
	}

	public void Interact(Player player, EscapeAction action)
	{
		if (network.IsOnline && !Multiplayer.IsServer())
			network.SendEscapeAction(action);
		else
			ApplyAction(player, action);
	}

	public void ApplyAction(Player player, EscapeAction action)
	{
		if (!IsAvailable || !world.GameplayStarted || Outcome != EscapeOutcome.InProgress || player == null)
			return;

		if (player.State == SurvivorState.InCar)
		{
			if (action == EscapeAction.Exit)
				ExitCar(player);
			else if (action == EscapeAction.Depart && CanDepart)
				EndMatch(EscapeOutcome.Escaped);
			return;
		}
		if (player.State != SurvivorState.OnFoot)
			return;

		switch (action)
		{
			case EscapeAction.PickUp when !Repaired && CarrierId == 0 &&
				(player.GlobalPosition + Vector3.Up * 1.25f).DistanceSquaredTo(starter.GlobalPosition) <= 3f * 3f:
				SetStarterCarrier(player.PeerId, starter.GlobalPosition);
				break;
			case EscapeAction.StartRepair when !Repaired && RepairerId == 0 &&
				CarrierId == player.PeerId && NearCar(player):
				RepairerId = player.PeerId;
				break;
			case EscapeAction.CancelRepair when RepairerId == player.PeerId:
				CancelRepair();
				break;
			case EscapeAction.Board when Repaired && NearCar(player):
				BoardCar(player);
				break;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!IsAvailable)
			return;

		if (Outcome == EscapeOutcome.Escaped)
		{
			drivingTime += (float)delta;
			float amount = Mathf.Min(drivingTime / DepartureSeconds, 1f);
			Vector3 next = parked + departureDirection * (amount * amount * 10f);
			Vector3 movement = next - car.GlobalPosition;
			car.GlobalPosition = next;
			foreach (Player player in world.Players)
			{
				if (player.State == SurvivorState.InCar)
					player.GlobalPosition += movement;
			}
			if (amount >= 1f && !resultShown)
				ShowResults();
			return;
		}

		if (RepairerId == 0 || (network.IsOnline && !Multiplayer.IsServer()))
			return;

		Player repairer = world.GetPlayer(RepairerId);
		if (repairer == null || repairer.State != SurvivorState.OnFoot || !NearCar(repairer))
		{
			CancelRepair();
			return;
		}

		RepairTime += (float)delta;
		if (RepairTime >= RepairSeconds)
		{
			RepairTime = RepairSeconds;
			RepairerId = 0;
			Repaired = true;
			SetStarterCarrier(0, starter.GlobalPosition);
		}
	}

	public string ObjectiveFor(Player player)
	{
		if (!IsAvailable || Outcome != EscapeOutcome.InProgress)
			return "";
		if (Repaired)
			return player.State == SurvivorState.InCar ? "" : "Get to the car";
		if (CarrierId == 0)
			return "Find the jump starter at the forester’s hut";
		return CarrierId == player.PeerId ? "Bring the jump starter to the car" : "Meet the carrier at the car";
	}

	public string HintFor(Player player, GodotObject target)
	{
		if (!IsAvailable)
			return "";
		if (target == starter && CarrierId == 0 && !Repaired)
			return "[E] Pick up jump starter";
		if (target != car)
			return "";
		if (Repaired)
			return "[E] Get in the car";
		if (RepairerId != 0 && RepairerId != player.PeerId)
			return "A teammate is repairing the car";
		return CarrierId == player.PeerId ? "[Hold E] Repair the car" : "The car needs a jump starter";
	}

	public EscapeAction? ActionFor(Player player, GodotObject target)
	{
		if (!IsAvailable)
			return null;
		if (target == starter && CarrierId == 0 && !Repaired)
			return EscapeAction.PickUp;
		if (target != car)
			return null;
		if (Repaired)
			return EscapeAction.Board;
		return CarrierId == player.PeerId && (RepairerId == 0 || RepairerId == player.PeerId)
			? EscapeAction.StartRepair : null;
	}

	public void Catch(Player player)
	{
		if (Outcome != EscapeOutcome.InProgress || player.State != SurvivorState.OnFoot)
			return;

		DropStarter(player);
		SetSurvivor(player, SurvivorState.Dead, -1, player.GlobalPosition, player.Rotation.Y);
		CheckLoss();
	}

	public void PlayerLeft(Player player)
	{
		if (IsAvailable && (!network.IsOnline || Multiplayer.IsServer()))
		{
			DropStarter(player);
			CheckLoss();
		}
	}

	private void CheckLoss()
	{
		if (world.GameplayStarted && Outcome == EscapeOutcome.InProgress &&
			world.Players.All(player => player.State == SurvivorState.Dead))
			EndMatch(EscapeOutcome.Lost);
	}

	private void DropStarter(Player player)
	{
		if (CarrierId != player.PeerId || Repaired)
			return;
		CancelRepair();
		SetStarterCarrier(0, player.GlobalPosition + Vector3.Up * 0.15f);
	}

	private void CancelRepair()
	{
		RepairerId = 0;
		RepairTime = 0f;
	}

	private bool NearCar(Player player)
	{
		Vector3 point = car.ToLocal(player.GlobalPosition + Vector3.Up * 0.85f);
		Vector3 nearest = point.Clamp(new Vector3(-0.9f, 0f, -2.1f), new Vector3(0.9f, 1.4f, 2.1f));
		return point.DistanceSquaredTo(nearest) <= 2.65f * 2.65f;
	}

	private void BoardCar(Player player)
	{
		for (int seat = 0; seat < SeatEyes.Length; seat++)
		{
			if (world.Players.Any(p => p.SeatIndex == seat))
				continue;

			Vector3 position = car.ToGlobal(SeatEyes[seat]) - Vector3.Up * player.ViewHeight;
			SetSurvivor(player, SurvivorState.InCar, seat, position, car.GlobalRotation.Y);
			return;
		}
	}

	private void ExitCar(Player player)
	{
		float side = player.SeatIndex == 1 ? 1f : -1f;
		Vector3 position = car.ToGlobal(new Vector3(side * 1.65f, 0.12f, player.SeatIndex == 2 ? 1.2f : 0.2f));
		SetSurvivor(player, SurvivorState.OnFoot, -1, position, car.GlobalRotation.Y);
	}

	private void SetSurvivor(Player player, SurvivorState state, int seat, Vector3 position, float yaw)
	{
		player.SetSurvivorState(state, seat, position, yaw);
		network.PublishSurvivor(player, state, seat, position, yaw);
	}

	private void EndMatch(EscapeOutcome outcome)
	{
		ApplyOutcome(outcome);
		network.PublishOutcome(outcome);
	}

	public void ApplyOutcome(EscapeOutcome outcome)
	{
		if (!IsAvailable || Outcome != EscapeOutcome.InProgress)
			return;
		Outcome = outcome;
		if (outcome == EscapeOutcome.Escaped)
			engine.Play();
		else
			ShowResults();
	}

	private void ShowResults()
	{
		resultShown = true;
		world.LocalPlayer?.ShowEscapeResult(Outcome);
	}

	public void SyncProgress(int carrier, int repairer, float seconds, bool fixedCar, Vector3 position)
	{
		if (!IsAvailable)
			return;
		RepairerId = repairer;
		RepairTime = seconds;
		Repaired = fixedCar;
		SetStarterCarrier(carrier, position);
	}

	private void SetStarterCarrier(int carrier, Vector3 position)
	{
		CarrierId = carrier;
		bool available = carrier == 0 && !Repaired;
		if (available && !starter.GlobalPosition.IsEqualApprox(position))
			starter.GlobalPosition = position;
		starter.Visible = available;
		starter.CollisionLayer = available ? 4u : 0u;
	}
}
