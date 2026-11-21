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
	private readonly Dictionary<int, string> lobby = new();
	private readonly HashSet<int> readyPeers = new();
	private readonly Dictionary<int, long> clockOffsets = new();
	private int[] matchPeers = System.Array.Empty<int>();
	private string nickname = "";
	private bool launching;
	private bool matchStarted;
	private long launchDeadline;
	private long startAt = -1;
	private long clockProbeAt = -1;
	private ENetMultiplayerPeer peer;
	private World world;
	private bool isHost;
	private double joinTimeRemaining;
	private double snapshotTimer;

	public bool IsOnline => peer != null;
	public bool IsHosting => isHost;
	public bool CanStart => isHost && !launching && !matchStarted && lobby.Count == GroupSize;
	public int GroupSize { get; private set; }
	public string[] MatchNames { get; private set; } = System.Array.Empty<string>();
	public string LocalNickname => nickname;
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

	public static bool IsValidNickname(string name)
	{
		name = name.Trim();
		if (name.Length < 1 || name.Length > 16)
			return false;

		foreach (char character in name)
		{
			if (!char.IsLetterOrDigit(character) && character != ' ' && character != '_' && character != '-')
				return false;
		}
		return true;
	}

	public Error PlaySolo(string name)
	{
		if (IsOnline || !IsValidNickname(name))
			return Error.InvalidParameter;
		nickname = name.Trim();
		MatchNames = new[] { nickname, "Pasażer z przodu", "Pasażer z tyłu" };
		Error result = GetTree().ChangeSceneToFile(WorldScene);
		if (result != Error.Ok)
			CloseSession();
		return result;
	}

	public Error CreateServer(string name, int groupSize)
	{
		if (IsOnline)
			return Error.AlreadyInUse;
		if (!IsValidNickname(name) || groupSize < 2 || groupSize > MaxPlayers)
			return Error.InvalidParameter;

		var connection = new ENetMultiplayerPeer();
		Error result = connection.CreateServer(Port, groupSize - 1);
		if (result != Error.Ok)
		{
			connection.Dispose();
			return result;
		}

		isHost = true;
		nickname = name.Trim();
		GroupSize = groupSize;
		lobby.Add(HostId, nickname);
		peer = connection;
		Multiplayer.MultiplayerPeer = peer;
		PublishLobby();
		return Error.Ok;
	}

	public void StartMatch()
	{
		if (!CanStart)
			return;

		peer.RefuseNewConnections = true;
		WorldSeed = GD.Randi();
		matchPeers = new int[lobby.Count];
		lobby.Keys.CopyTo(matchPeers, 0);
		System.Array.Sort(matchPeers);
		MatchNames = System.Array.ConvertAll(matchPeers, id => lobby[id]);
		BeginLoading();
		Rpc(MethodName.OpenWorld, WorldSeed, matchPeers, MatchNames);
		LoadWorld();
	}

	public void LeaveLobby()
	{
		CloseSession();
		SetMenuStatus("", false);
	}

	public Error JoinServer(string address, string name)
	{
		if (IsOnline)
			return Error.AlreadyInUse;

		address = address.Trim();
		if (!IsValidNickname(name) || !System.Net.IPAddress.TryParse(address, out var ip))
			return Error.InvalidParameter;

		var connection = new ENetMultiplayerPeer();
		Error result = connection.CreateClient(ip.ToString(), Port);
		if (result != Error.Ok)
		{
			connection.Dispose();
			return result;
		}

		peer = connection;
		nickname = name.Trim();
		isHost = false;
		joinTimeRemaining = JoinTimeout;
		Multiplayer.MultiplayerPeer = peer;
		SetMenuStatus($"Łączenie z {address}…", true);
		return Error.Ok;
	}

	public void RegisterWorld(World activeWorld)
	{
		world = activeWorld;
		int localId = Multiplayer.GetUniqueId();
		for (int slot = 0; slot < matchPeers.Length; slot++)
		{
			int id = matchPeers[slot];
			playerSlots.Add(id, slot);
			world.SpawnPlayer(id, world.StartPosition(slot), id == localId, MatchNames[slot]);
		}
		if (isHost)
		{
			readyPeers.Add(HostId);
			ProbeClocksWhenReady();
		}
		else
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
		if (joinTimeRemaining > 0)
		{
			joinTimeRemaining -= delta;
			if (joinTimeRemaining <= 0)
				JoinFailed("Brak odpowiedzi hosta. Sprawdź adres i połączenie.");
		}

		long now = (long)Time.GetTicksMsec();
		if (startAt >= 0 && now >= startAt)
		{
			launching = false;
			matchStarted = true;
			launchDeadline = 0;
			world.StartOpening(startAt);
			startAt = -1;
		}
		else if (launchDeadline > 0 && now >= launchDeadline)
			AbortStart("Nie wszyscy gracze załadowali las.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!matchStarted || !IsInstanceValid(world))
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
		if (!isHost && id == HostId)
			RpcId(HostId, MethodName.RegisterNickname, nickname);
	}

	private void OnPeerDisconnected(long id)
	{
		if (!isHost || !lobby.Remove((int)id))
			return;

		if (launching)
		{
			AbortStart("Gracz opuścił lobby w trakcie wczytywania.");
			return;
		}
		if (!matchStarted)
		{
			PublishLobby();
			return;
		}
		playerSlots.Remove((int)id);
		world?.RemovePlayer((int)id);
		Rpc(MethodName.DespawnPlayer, (int)id);
	}

	private void OnConnectionFailed() => JoinFailed("Nie udało się połączyć z hostem.");

	private void OnServerDisconnected() => AbortStart("Host się rozłączył.");

	private void AbortStart(string message)
	{
		startAt = -1;
		launchDeadline = 0;
		if (IsInstanceValid(world) || launching || matchStarted)
			CallDeferred(nameof(ReturnToTitle), message);
		else
			JoinFailed(message);
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
		lobby.Clear();
		readyPeers.Clear();
		clockOffsets.Clear();
		matchPeers = System.Array.Empty<int>();
		MatchNames = System.Array.Empty<string>();
		GroupSize = 0;
		isHost = false;
		launching = false;
		matchStarted = false;
		launchDeadline = 0;
		startAt = -1;
		clockProbeAt = -1;
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

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RegisterNickname(string name)
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (!isHost || launching || matchStarted || id <= HostId || lobby.ContainsKey(id))
			return;

		if (!IsValidNickname(name) || lobby.Count >= GroupSize)
		{
			peer.DisconnectPeer(id);
			return;
		}
		lobby.Add(id, name.Trim());
		PublishLobby();
	}

	private void PublishLobby()
	{
		var names = new string[lobby.Count];
		lobby.Values.CopyTo(names, 0);
		foreach (int id in lobby.Keys)
		{
			if (id != HostId)
				RpcId(id, MethodName.ReceiveLobby, GroupSize, names);
		}
		ShowRoster(names);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ReceiveLobby(int size, string[] names)
	{
		if (!IsOnline || isHost || launching)
			return;
		joinTimeRemaining = 0;
		GroupSize = size;
		ShowRoster(names);
	}

	private void ShowRoster(string[] names)
	{
		string status = $"Lobby LAN · {names.Length}/{GroupSize}\n{string.Join("  ·  ", names)}";
		if (names.Length < GroupSize)
			status += "\nCzekanie na graczy…";
		else if (!isHost)
			status += "\nCzekanie na start hosta…";
		SetMenuStatus(status, false);
	}

	private void BeginLoading()
	{
		launching = true;
		launchDeadline = (long)Time.GetTicksMsec() + 30000;
		SetMenuStatus("Wczytywanie lasu…", true);
	}

	private void LoadWorld()
	{
		if (GetTree().ChangeSceneToFile(WorldScene) != Error.Ok)
			JoinFailed("Nie udało się wczytać lasu.");
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void OpenWorld(long seed, int[] ids, string[] names)
	{
		if (!IsOnline || isHost || launching || GroupSize < 2 || ids.Length != GroupSize ||
			names.Length != ids.Length || System.Array.IndexOf(ids, Multiplayer.GetUniqueId()) < 0)
			return;

		WorldSeed = seed;
		matchPeers = ids;
		MatchNames = names;
		BeginLoading();
		LoadWorld();
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ClientReady()
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (!isHost || !launching || id <= HostId || !lobby.ContainsKey(id))
			return;
		readyPeers.Add(id);
		ProbeClocksWhenReady();
	}

	private void ProbeClocksWhenReady()
	{
		if (readyPeers.Count != GroupSize || clockProbeAt >= 0)
			return;
		clockProbeAt = (long)Time.GetTicksMsec();
		Rpc(MethodName.ReadClock);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ReadClock()
	{
		if (launching && !isHost && IsInstanceValid(world))
			RpcId(HostId, MethodName.ReportClock, (long)Time.GetTicksMsec());
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ReportClock(long clientTick)
	{
		int id = Multiplayer.GetRemoteSenderId();
		if (!isHost || !launching || clockProbeAt < 0 || startAt >= 0 || id <= HostId ||
			!readyPeers.Contains(id) || clientTick < 0 || clientTick > long.MaxValue / 2)
			return;

		long now = (long)Time.GetTicksMsec();
		// Estimate the host tick when the client sampled its clock using half the round trip.
		clockOffsets.TryAdd(id, clientTick - (clockProbeAt + now) / 2);
		if (clockOffsets.Count != GroupSize - 1)
			return;

		startAt = now + 1500;
		foreach (var clock in clockOffsets)
			RpcId(clock.Key, MethodName.ScheduleStart, startAt + clock.Value);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ScheduleStart(long localTick)
	{
		if (launching && !isHost && IsInstanceValid(world))
			startAt = System.Math.Max(0, localTick);
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
