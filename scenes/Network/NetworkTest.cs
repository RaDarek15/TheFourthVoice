using Godot;

public partial class NetworkTest : Node
{
	public override void _Ready()
	{
		GD.Print("NETWORK TEST START");

		NetworkManager network =
			GetNode<NetworkManager>("/root/NetworkManager");

		GD.Print("NETWORK MANAGER FOUND");

		Error result = network.CreateServer();

		GD.Print($"HOST RESULT: {result}");
	}
}
