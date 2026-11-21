
using Godot;
using System.Collections.Generic;

namespace LegacyNetwork;

public partial class NetworkManager : Node
{
	// Konfiguracja sieci
	private const int Port = 7777;
	private const int MaxPlayers = 3;
	private const int MaxClients = MaxPlayers - 1;

	// Aktualnie połączone identyfikatory peerów.
	private readonly HashSet<long> _connectedPeerIds = new();

	// Liczba aktualnie połączonych graczy.
	private int _currentPlayerCount = 0;

	// Informacja, czy rozpoczęliśmy sesję sieciową.
	private bool _isNetworkActive = false;

	// Publiczny dostęp do liczby graczy.
	public int CurrentPlayerCount => _currentPlayerCount;

	// Sygnały dostępne dla innych nodów i interfejsu.
	[Signal]
	public delegate void PlayerCountChangedEventHandler(int playerCount);

	[Signal]
	public delegate void NetworkConnectedEventHandler();

	[Signal]
	public delegate void NetworkConnectionFailedEventHandler();

	[Signal]
	public delegate void NetworkDisconnectedEventHandler();


	public override void _Ready()
	{
		// Podłączanie sygnałów MultiplayerAPI.
		Multiplayer.PeerConnected += OnPeerConnected;
		Multiplayer.PeerDisconnected += OnPeerDisconnected;

		Multiplayer.ConnectedToServer += OnConnectedToServer;
		Multiplayer.ConnectionFailed += OnConnectionFailed;
		Multiplayer.ServerDisconnected += OnServerDisconnected;

		GD.Print("NetworkManager initialized.");
	}


	// =========================================
	// HOST
	// =========================================

	public Error CreateServer()
	{
		if (_isNetworkActive)
		{
			GD.PushWarning("Network session is already active.");
			return Error.AlreadyInUse;
		}

		ENetMultiplayerPeer peer = new ENetMultiplayerPeer();

		// Tworzenie serwera ENet.
		Error error = peer.CreateServer(Port, MaxClients);

		if (error != Error.Ok)
		{
			GD.PushError($"Failed to create server: {error}");
			return error;
		}

		// Przypisanie ENet do systemu multiplayer Godota.
		Multiplayer.MultiplayerPeer = peer;

		_isNetworkActive = true;

		// Serwer zawsze ma ID 1.
		_connectedPeerIds.Add(1);

		UpdatePlayerCount();

		GD.Print($"Server started on port {Port}.");
		GD.Print($"Maximum players: {MaxPlayers}");

		EmitSignal(SignalName.NetworkConnected);

		return Error.Ok;
	}


	// =========================================
	// CLIENT
	// =========================================

	public Error JoinServer(string ipAddress)
	{
		if (_isNetworkActive)
		{
			GD.PushWarning("Network session is already active.");
			return Error.AlreadyInUse;
		}

		if (string.IsNullOrWhiteSpace(ipAddress))
		{
			GD.PushError("IP address cannot be empty.");
			return Error.InvalidParameter;
		}

		ENetMultiplayerPeer peer = new ENetMultiplayerPeer();

		// Próba utworzenia połączenia z serwerem.
		Error error = peer.CreateClient(ipAddress.Trim(), Port);

		if (error != Error.Ok)
		{
			GD.PushError($"Failed to create client: {error}");
			return error;
		}

		Multiplayer.MultiplayerPeer = peer;

		_isNetworkActive = true;

		GD.Print($"Connecting to server: {ipAddress}:{Port}");

		return Error.Ok;
	}


	// =========================================
	// DISCONNECT
	// =========================================

	public void DisconnectFromNetwork()
	{
		if (!_isNetworkActive)
		{
			return;
		}

		// Zakończenie połączenia i wyłączenie MultiplayerPeer.
		Multiplayer.MultiplayerPeer = null;

		_isNetworkActive = false;

		_connectedPeerIds.Clear();

		UpdatePlayerCount();

		GD.Print("Disconnected from network.");

		EmitSignal(SignalName.NetworkDisconnected);
	}


	// =========================================
	// MULTIPLAYER SIGNALS
	// =========================================

	private void OnPeerConnected(long peerId)
	{
		GD.Print($"Peer connected: {peerId}");

		_connectedPeerIds.Add(peerId);

		UpdatePlayerCount();
	}


	private void OnPeerDisconnected(long peerId)
	{
		GD.Print($"Peer disconnected: {peerId}");

		_connectedPeerIds.Remove(peerId);

		UpdatePlayerCount();
	}


	private void OnConnectedToServer()
	{
		GD.Print("Successfully connected to server.");

		// Dodajemy lokalnego klienta do listy graczy.
		long localPeerId = Multiplayer.GetUniqueId();

		_connectedPeerIds.Add(localPeerId);

		UpdatePlayerCount();

		EmitSignal(SignalName.NetworkConnected);
	}


	private void OnConnectionFailed()
	{
		GD.PushError("Connection to server failed.");

		ResetNetworkState();

		EmitSignal(SignalName.NetworkConnectionFailed);
	}


	private void OnServerDisconnected()
	{
		GD.PushWarning("Server disconnected.");

		ResetNetworkState();

		EmitSignal(SignalName.NetworkDisconnected);
	}


	// =========================================
	// PLAYER COUNT
	// =========================================

	private void UpdatePlayerCount()
	{
		_currentPlayerCount = _connectedPeerIds.Count;

		GD.Print($"Current players: {_currentPlayerCount}/{MaxPlayers}");

		EmitSignal(
			SignalName.PlayerCountChanged,
			_currentPlayerCount
		);
	}


	// =========================================
	// RESET
	// =========================================

	private void ResetNetworkState()
	{
		Multiplayer.MultiplayerPeer = null;

		_isNetworkActive = false;

		_connectedPeerIds.Clear();

		UpdatePlayerCount();

		GD.Print("Network state reset.");
	}
}
