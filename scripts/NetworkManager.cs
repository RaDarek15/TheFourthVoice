using Godot;
using System.Collections.Generic;

public partial class NetworkManager : Node
{
	public const int HostId = 1;
	public const int Port = 7777;
	public const int MaxPlayers = 3;
	public const float SpawnSpacing = 2f;

	private const string WorldScene = "res://scenes/world.tscn";
	private const string TitleScene = "res://scenes/title_screen.tscn";
	private const double JoinTimeout = 10.0;
	private const double SnapshotInterval = 0.05;

	private readonly Dictionary<int, int> playerSlots = new();
	private ENetMultiplayerPeer peer;
	private World world;
	private bool isHost;
	private double joinTimeRemaining;
	private double snapshotTimer;

	public bool IsOnline => peer != null;
	public long WorldSeed { get; private set; }
	public string MenuMessage { get; private set; } = "";

	[Signal]
	public delegate void MenuStatusChangedEventHandler(string message, bool busy);

	public override void _Ready()
	{
		Multiplayer.PeerConnected += OnPeerConnected;
		Multiplayer.PeerDisconnected += OnPeerDisconnected;
		Multiplayer.ConnectionFailed += OnConnectionFailed;
		Multiplayer.ServerDisconnected += OnServerDisconnected;
	}

	public Error CreateServer()
	{
		if (IsOnline)
			return Error.AlreadyInUse;

		var connection = new ENetMultiplayerPeer();
		Error result = connection.CreateServer(Port, MaxPlayers - 1);
		if (result != Error.Ok)
		{
			connection.Dispose();
			return result;
		}

		isHost = true;
		WorldSeed = GD.Randi();
		peer = connection;
		Multiplayer.MultiplayerPeer = peer;
		result = GetTree().ChangeSceneToFile(WorldScene);
		if (result != Error.Ok)
			CloseSession();
		return result;
	}

	public Error JoinServer(string address)
	{
		if (IsOnline)
			return Error.AlreadyInUse;

		address = address.Trim();
		if (!System.Net.IPAddress.TryParse(address, out var ip))
			return Error.InvalidParameter;

		var connection = new ENetMultiplayerPeer();
		Error result = connection.CreateClient(ip.ToString(), Port);
		if (result != Error.Ok)
		{
			connection.Dispose();
			return result;
		}

		peer = connection;
		isHost = false;
		joinTimeRemaining = JoinTimeout;
		Multiplayer.MultiplayerPeer = peer;
		SetMenuStatus($"Connecting to {address}…", true);
		return Error.Ok;
	}

	public void RegisterWorld(World activeWorld)
	{
		world = activeWorld;
		if (isHost)
		{
			playerSlots.Add(HostId, 0);
			world.SpawnPlayer(HostId, world.SpawnPosition, true);
		}
		else if (IsOnline)
			RpcId(HostId, MethodName.ClientReady);
	}

	public void UnregisterWorld(World activeWorld)
	{
		if (world == activeWorld)
			world = null;
	}

	public void ReturnToTitle(string message = "")
	{
		CloseSession();
		SetMenuStatus(message, false);
		GetTree().ChangeSceneToFile(TitleScene);
	}

	public override void _Process(double delta)
	{
		if (joinTimeRemaining <= 0)
			return;

		joinTimeRemaining -= delta;
		if (joinTimeRemaining <= 0)
			JoinFailed("Connection timed out. Check the host IP and firewall.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!IsOnline || !IsInstanceValid(world))
			return;

		snapshotTimer += delta;
		if (snapshotTimer < SnapshotInterval)
			return;
		snapshotTimer %= SnapshotInterval;

		if (isHost)
		{
			foreach (int recipient in playerSlots.Keys)
			{
				if (recipient == HostId)
					continue;

				foreach (Player player in world.Players)
				{
					if (player.PeerId != recipient)
						SendPlayer(recipient, MethodName.SyncPlayer, player);
				}

				Stalker stalker = world.Enemy;
				if (IsInstanceValid(stalker) && stalker.IsPlaced)
					RpcId(recipient, MethodName.SyncStalker, stalker.GlobalPosition, stalker.Rotation.Y, stalker.IsWalking);
				if (IsInstanceValid(world.Door))
					RpcId(recipient, MethodName.SyncDoor, world.Door.Rotation.Y, world.Door.IsOpen);
			}
		}
		else if (IsInstanceValid(world.LocalPlayer))
		{
			Player player = world.LocalPlayer;
			RpcId(HostId, MethodName.SubmitPlayer, player.GlobalPosition, player.Rotation.Y,
				player.HeadPitch, player.HoldingFlashlight, player.IsFlashlightOn, player.IsWalking, player.IsRunning);
		}
	}

	private void OnPeerConnected(long id)
	{
		if (isHost)
			RpcId(id, MethodName.OpenWorld, WorldSeed);
	}

	private void OnPeerDisconnected(long id)
	{
		if (!isHost || !playerSlots.Remove((int)id))
			return;

		world?.RemovePlayer((int)id);
		foreach (int recipient in playerSlots.Keys)
		{
			if (recipient != HostId)
				RpcId(recipient, MethodName.DespawnPlayer, (int)id);
		}
	}

	private void OnConnectionFailed() => JoinFailed("Could not connect. Check the host IP and firewall.");

	private void OnServerDisconnected()
	{
		if (IsInstanceValid(world))
			ReturnToTitle("The host disconnected.");
		else
			JoinFailed("The host disconnected.");
	}

	private void JoinFailed(string message)
	{
		CloseSession();
		SetMenuStatus(message, false);
	}

	private void CloseSession()
	{
		bool hadConnection = peer != null;
		peer?.Close();
		peer = null;
		if (hadConnection)
			Multiplayer.MultiplayerPeer = null;
		playerSlots.Clear();
		isHost = false;
		WorldSeed = 0;
		joinTimeRemaining = 0;
		snapshotTimer = 0;
	}

	private void SetMenuStatus(string message, bool busy)
	{
		MenuMessage = message;
		EmitSignal(SignalName.MenuStatusChanged, message, busy);
	}

	private void SendPlayer(int recipient, StringName method, Player player)
	{
		RpcId(recipient, method, player.PeerId, player.GlobalPosition, player.Rotation.Y,
			player.HeadPitch, player.HoldingFlashlight, player.IsFlashlightOn, player.IsWalking, player.IsRunning);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void OpenWorld(long seed)
	{
		if (!IsOnline || isHost || joinTimeRemaining <= 0)
			return;

		joinTimeRemaining = 0;
		WorldSeed = seed;
		SetMenuStatus("Loading the forest…", true);
		if (GetTree().ChangeSceneToFile(WorldScene) != Error.Ok)
			JoinFailed("Could not open the forest.");
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientReady()
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (!isHost || id <= HostId || !IsInstanceValid(world) || playerSlots.ContainsKey(id))
			return;

		for (int slot = 1; slot < MaxPlayers; slot++)
		{
			if (playerSlots.ContainsValue(slot))
				continue;

			float side = slot == 1 ? 1f : -1f;
			Player newcomer = world.SpawnPlayer(id, world.SpawnPosition + Vector3.Right * (SpawnSpacing * side), false);
			foreach (Player player in world.Players)
				SendPlayer(id, MethodName.SpawnPlayer, player);

			foreach (int recipient in playerSlots.Keys)
			{
				if (recipient != HostId)
					SendPlayer(recipient, MethodName.SpawnPlayer, newcomer);
			}
			playerSlots.Add(id, slot);
			return;
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void SpawnPlayer(int id, Vector3 position, float yaw, float pitch, bool equipped, bool flashlight, bool walking, bool running)
	{
		if (!IsInstanceValid(world) || world.GetPlayer(id) != null)
			return;

		var player = world.SpawnPlayer(id, position, id == Multiplayer.GetUniqueId());
		player.ApplyPose(position, yaw, pitch, equipped, flashlight, walking, running, snap: true);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void DespawnPlayer(int id) => world?.RemovePlayer(id);

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void SubmitPlayer(Vector3 position, float yaw, float pitch, bool equipped, bool flashlight, bool walking, bool running)
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (isHost && playerSlots.ContainsKey(id) && position.IsFinite() && float.IsFinite(yaw) && float.IsFinite(pitch))
			world?.GetPlayer(id)?.ApplyPose(position, yaw, pitch, equipped, flashlight, walking, running);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void SyncPlayer(int id, Vector3 position, float yaw, float pitch, bool equipped, bool flashlight, bool walking, bool running)
	{
		Player player = world?.GetPlayer(id);
		if (player != null && !player.IsLocalPlayer)
			player.ApplyPose(position, yaw, pitch, equipped, flashlight, walking, running);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void SyncStalker(Vector3 position, float yaw, bool walking) => world?.Enemy?.ApplyPose(position, yaw, walking);

	public void Whistle(Player player)
	{
		if (!player.TryStartWhistle())
			return;

		Vector3 origin = player.GlobalPosition;
		player.PlayWhistle(origin);
		if (!IsOnline || isHost)
			ShareWhistle(player, origin);
		else
			RpcId(HostId, MethodName.RequestWhistle, origin);
	}

	private void ShareWhistle(Player player, Vector3 origin)
	{
		World activeWorld = IsOnline ? world : player.GetParent() as World;
		Stalker stalker = activeWorld?.Enemy;
		if (IsInstanceValid(stalker))
			stalker.InvestigateWhistle(origin);

		if (!isHost)
			return;

		foreach (int recipient in playerSlots.Keys)
		{
			if (recipient != HostId && recipient != player.PeerId)
				RpcId(recipient, MethodName.HearWhistle, player.PeerId, origin);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestWhistle(Vector3 origin)
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (!isHost || !playerSlots.ContainsKey(id) || !origin.IsFinite())
			return;

		Player player = world?.GetPlayer(id);
		if (!IsInstanceValid(player) || !player.TryStartWhistle())
			return;

		// The reliable whistle can arrive ahead of the latest movement snapshot.
		if (player.GlobalPosition.DistanceSquaredTo(origin) > 9f)
			origin = player.GlobalPosition;

		player.PlayWhistle(origin);
		ShareWhistle(player, origin);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void HearWhistle(int id, Vector3 origin) => world?.GetPlayer(id)?.PlayWhistle(origin);

	public void ToggleDoor(HutDoor door)
	{
		if (!IsOnline || isHost)
			door.Toggle();
		else if (door == world?.Door)
			RpcId(HostId, MethodName.RequestDoorToggle);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestDoorToggle()
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (!isHost || !playerSlots.ContainsKey(id) || world?.Door == null)
			return;

		Player player = world.GetPlayer(id);
		if (player != null && (player.GlobalPosition + Vector3.Up).DistanceSquaredTo(world.Door.GlobalPosition) <= 12.25f)
			world.Door.Toggle();
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void SyncDoor(float angle, bool open) => world?.Door?.ApplyNetworkState(angle, open);
}
