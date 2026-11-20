
using Godot;
using System.Collections.Generic;

public partial class World : Node3D
{
	[Export] public Mesh TreeMesh;
	[Export] public Node3D Player;

	[Export] public int TreeCount = 150;
	[Export] public float ForestSize = 90f;
	[Export] public float RenderDistance = 10f;
	[Export] public float UpdateInterval = 0.2f;

	private const float CellSize = 20f;

	private MultiMesh multiMesh;
	private Transform3D[] uploadedTrees;
	private readonly Dictionary<Vector2I, List<Transform3D>> treeCells = new();
	private readonly List<Transform3D> visibleTrees = new();

	private float updateTimer = 0f;

	public override void _Ready()
	{
		if (TreeMesh == null || Player == null)
		{
			GD.PrintErr("Assign TreeMesh and Player!");
			SetProcess(false);
			return;
		}

		GenerateTrees();
		int capacity = Mathf.Max(0, TreeCount);
		uploadedTrees = new Transform3D[capacity];

		multiMesh = new MultiMesh
		{
			TransformFormat =
				MultiMesh.TransformFormatEnum.Transform3D,

			Mesh = TreeMesh,
			InstanceCount = capacity,
			VisibleInstanceCount = 0
		};

		UpdateVisibleTrees();
		AddChild(new MultiMeshInstance3D
		{
			Name = "Forest",
			Multimesh = multiMesh
		});
	}

	private void GenerateTrees()
	{
		var random = new RandomNumberGenerator();

		for (int i = 0; i < TreeCount; i++)
		{
			float x = random.RandfRange(
				-ForestSize / 2f,
				ForestSize / 2f
			);

			float z = random.RandfRange(
				-ForestSize / 2f,
				ForestSize / 2f
			);

			Vector3 position = new(x, 0, z);

			float rotation = random.RandfRange(
				0f,
				Mathf.Tau
			);

			float scale = random.RandfRange(
				0.8f,
				1.2f
			);

			Basis basis = new Basis(
				Vector3.Up,
				rotation
			).Scaled(Vector3.One * scale);

			Vector2I cell = GetCell(ToGlobal(position));
			if (!treeCells.TryGetValue(cell, out var trees))
			{
				trees = new List<Transform3D>();
				treeCells.Add(cell, trees);
			}

			trees.Add(new Transform3D(basis, position));
		}
	}

	public override void _Process(double delta)
	{
		updateTimer += (float)delta;

		if (updateTimer >= UpdateInterval)
		{
			updateTimer = 0f;
			UpdateVisibleTrees();
		}
	}

	private void UpdateVisibleTrees()
	{
		Vector3 playerPosition = Player.GlobalPosition;
		float distance = Mathf.Max(0f, RenderDistance);
		float maxDistanceSquared = distance * distance;
		Vector3 searchExtent = new(distance, 0f, distance);
		Vector2I minCell = GetCell(playerPosition - searchExtent);
		Vector2I maxCell = GetCell(playerPosition + searchExtent);
		visibleTrees.Clear();

		for (int z = minCell.Y; z <= maxCell.Y; z++)
		{
			for (int x = minCell.X; x <= maxCell.X; x++)
			{
				if (!treeCells.TryGetValue(new Vector2I(x, z), out var trees))
					continue;

				foreach (Transform3D tree in trees)
				{
					Vector3 offset = ToGlobal(tree.Origin) - playerPosition;
					if (offset.X * offset.X + offset.Z * offset.Z <= maxDistanceSquared)
						visibleTrees.Add(tree);
				}
			}
		}

		for (int i = 0; i < visibleTrees.Count; i++)
		{
			Transform3D tree = visibleTrees[i];
			if (uploadedTrees[i] == tree)
				continue;

			multiMesh.SetInstanceTransform(i, tree);
			uploadedTrees[i] = tree;
		}

		if (multiMesh.VisibleInstanceCount != visibleTrees.Count)
			multiMesh.VisibleInstanceCount = visibleTrees.Count;
	}

	private static Vector2I GetCell(Vector3 position) => new(
		Mathf.FloorToInt(position.X / CellSize),
		Mathf.FloorToInt(position.Z / CellSize));
}
