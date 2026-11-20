
using Godot;

public partial class World : Node3D
{
	[Export] public PackedScene TreeScene;

	[Export] public int TreeCount = 150;
	[Export] public float ForestSize = 90f;
	[Export] public float SpawnClearRadius = 5f;

	public override void _Ready()
	{
		GenerateForest();
	}

	private void GenerateForest()
	{
		
		if (TreeScene == null)
		{
			GD.PrintErr("TreeScene is not assigned!");
			return;
		}

		RandomNumberGenerator random = new();

		for (int i = 0; i < TreeCount; i++)
		{
			float x = random.RandfRange(
				-ForestSize / 2,
				ForestSize / 2
			);

			float z = random.RandfRange(
				-ForestSize / 2,
				ForestSize / 2
			);

			Vector2 position = new(x, z);

			if (position.Length() < SpawnClearRadius)
				continue;

			Node3D tree = TreeScene.Instantiate<Node3D>();

			AddChild(tree);

			tree.Position = new Vector3(x, 0, z);
		}
	}
}
