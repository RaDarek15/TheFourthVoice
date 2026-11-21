using Godot;
using System.Collections.Generic;

public partial class World : Node3D
{
	[Export] public Mesh TreeMesh;
	[Export] public PackedScene CrookedTreeModel;
	[Export] public Node3D Player;
	[Export] public Node3D Terrain;

	[Export] public int TreeCount = 150;
	[Export] public float ForestSize = 90f;
	[Export] public float RenderDistance = 10f;
	[Export] public float UpdateInterval = 0.2f;

	private const float CellSize = 20f;
	private const float SpawnClearance = 1.5f;

	private readonly record struct Tree(Transform3D Transform, int Variant);
	private readonly record struct PlantingTile(Aabb Bounds, Vector3[] Faces);

	private sealed class TreeBatch
	{
		public readonly MultiMesh Mesh;
		public readonly Transform3D[] Uploaded;
		public int VisibleCount;

		public TreeBatch(Mesh mesh, int capacity)
		{
			Mesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = mesh,
				InstanceCount = capacity,
				VisibleInstanceCount = 0
			};
			Uploaded = new Transform3D[capacity];
		}
	}

	private readonly List<TreeBatch> batches = new();
	private readonly Dictionary<Vector2I, List<Tree>> treeCells = new();
	private float updateTimer;

	public override void _Ready()
	{
		if (TreeMesh == null || Player == null)
		{
			GD.PrintErr("Assign TreeMesh and Player!");
			SetProcess(false);
			return;
		}

		AddBatch("Forest", TreeMesh);
		if (CrookedTreeModel != null)
		{
			Node model = CrookedTreeModel.Instantiate();
			var mesh = new ArrayMesh();
			AppendMeshParts(model, Transform3D.Identity, mesh);
			model.Free();
			if (mesh.GetSurfaceCount() > 0)
				AddBatch("CrookedPines", mesh);
		}

		GenerateTrees();
		UpdateVisibleTrees();
	}

	private void AddBatch(string name, Mesh mesh)
	{
		var batch = new TreeBatch(mesh, Mathf.Max(0, TreeCount));
		batches.Add(batch);
		AddChild(new MultiMeshInstance3D { Name = name, Multimesh = batch.Mesh });
	}

	private static void AppendMeshParts(Node node, Transform3D transform, ArrayMesh output)
	{
		if (node is Node3D spatial)
			transform *= spatial.Transform;

		if (node is MeshInstance3D instance && instance.Mesh is Mesh mesh)
		{
			using var surface = new SurfaceTool();
			for (int i = 0; i < mesh.GetSurfaceCount(); i++)
			{
				surface.Begin(Mesh.PrimitiveType.Triangles);
				surface.AppendFrom(mesh, i, transform);
				surface.SetMaterial(instance.GetActiveMaterial(i));
				surface.Commit(output);
			}
		}

		foreach (Node child in node.GetChildren())
			AppendMeshParts(child, transform, output);
	}

	private void GenerateTrees()
	{
		var random = new RandomNumberGenerator();
		List<PlantingTile> plantingTiles = GetPlantingTiles();
		var tallTrunk = new CylinderShape3D { Height = 3f, Radius = 0.25f };
		var crookedBase = new CylinderShape3D { Height = 1.72f, Radius = 0.38f };
		var crookedUpper = new CylinderShape3D { Height = 1.08f, Radius = 0.32f };
		Vector3 spawn = ToLocal(Player.GlobalPosition);
		var colliders = new Node3D { Name = "TreeColliders" };
		AddChild(colliders);

		float forestRadius = ForestSize / 2f;
		int planted = 0;
		for (long attempt = 0; attempt < 3L * TreeCount && planted < TreeCount; attempt++)
		{
			float x = random.RandfRange(-forestRadius, forestRadius);
			float z = random.RandfRange(-forestRadius, forestRadius);
			var position = new Vector3(x, 0f, z);
			if (new Vector2(x - spawn.X, z - spawn.Z).LengthSquared() < SpawnClearance * SpawnClearance)
				continue;

			if (Terrain != null && !TryGetPlantingPosition(plantingTiles, ref position))
				continue;

			planted++;
			int variant = random.RandiRange(0, batches.Count - 1);
			float rotation = random.RandfRange(0f, Mathf.Tau);
			float scale = random.RandfRange(0.8f, 1.2f);
			Basis basis = new Basis(Vector3.Up, rotation).Scaled(Vector3.One * scale);
			Vector2I cell = GetCell(ToGlobal(position));
			if (!treeCells.TryGetValue(cell, out var trees))
			{
				trees = new List<Tree>();
				treeCells.Add(cell, trees);
			}

			trees.Add(new Tree(new Transform3D(basis, position), variant));

			var body = new StaticBody3D { Position = position, Rotation = new Vector3(0f, rotation, 0f) };
			if (variant == 0)
				AddTrunkShape(body, tallTrunk, new Vector3(0f, 1.5f, 0f), 0f, scale);
			else
			{
				AddTrunkShape(body, crookedBase, new Vector3(-0.42f, 0.75f, 0.05f), 0.51f, scale);
				AddTrunkShape(body, crookedUpper, new Vector3(-0.76f, 1.99f, 0.05f), -0.22f, scale);
			}
			colliders.AddChild(body);
		}
	}

	private List<PlantingTile> GetPlantingTiles()
	{
		var tiles = new List<PlantingTile>();
		if (Terrain == null)
			return tiles;

		Transform3D worldToLocal = GlobalTransform.AffineInverse();
		foreach (Node child in Terrain.FindChildren("*", "MeshInstance3D", owned: false))
		{
			var tile = (MeshInstance3D)child;
			Mesh mesh = tile.Mesh;
			if (mesh == null)
				continue;

			Transform3D localTransform = worldToLocal * tile.GlobalTransform;
			var faces = new List<Vector3>();
			for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
			{
				if (mesh.SurfaceGetMaterial(surface)?.ResourceName != "M_ForestLitter")
					continue;

				var arrays = mesh.SurfaceGetArrays(surface);
				Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
				int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
				if (indices.Length == 0)
				{
					foreach (Vector3 vertex in vertices)
						faces.Add(localTransform * vertex);
				}
				else
				{
					foreach (int index in indices)
						faces.Add(localTransform * vertices[index]);
				}
			}

			if (faces.Count > 0)
				tiles.Add(new PlantingTile(localTransform * mesh.GetAabb(), faces.ToArray()));
		}
		return tiles;
	}

	private static bool TryGetPlantingPosition(List<PlantingTile> tiles, ref Vector3 position)
	{
		foreach (PlantingTile tile in tiles)
		{
			Aabb bounds = tile.Bounds;
			if (position.X < bounds.Position.X || position.X > bounds.End.X ||
				position.Z < bounds.Position.Z || position.Z > bounds.End.Z)
				continue;

			Vector3 from = new(position.X, bounds.End.Y + 1f, position.Z);
			Vector3 to = new(position.X, bounds.Position.Y - 1f, position.Z);
			Vector3[] faces = tile.Faces;
			for (int i = 0; i < faces.Length; i += 3)
			{
				Variant hit = Geometry3D.SegmentIntersectsTriangle(from, to, faces[i], faces[i + 1], faces[i + 2]);
				if (hit.VariantType == Variant.Type.Vector3)
				{
					position = hit.AsVector3();
					return true;
				}
			}
		}
		return false;
	}

	private static void AddTrunkShape(StaticBody3D body, Shape3D shape, Vector3 center, float tilt, float scale)
	{
		body.AddChild(new CollisionShape3D
		{
			Shape = shape,
			Position = center * scale,
			Rotation = new Vector3(0f, 0f, tilt),
			Scale = Vector3.One * scale
		});
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

		foreach (TreeBatch batch in batches)
			batch.VisibleCount = 0;

		for (int z = minCell.Y; z <= maxCell.Y; z++)
		{
			for (int x = minCell.X; x <= maxCell.X; x++)
			{
				if (!treeCells.TryGetValue(new Vector2I(x, z), out var trees))
					continue;

				foreach (Tree tree in trees)
				{
					Vector3 offset = ToGlobal(tree.Transform.Origin) - playerPosition;
					if (offset.X * offset.X + offset.Z * offset.Z > maxDistanceSquared)
						continue;

					TreeBatch batch = batches[tree.Variant];
					int index = batch.VisibleCount++;
					if (batch.Uploaded[index] == tree.Transform)
						continue;

					batch.Mesh.SetInstanceTransform(index, tree.Transform);
					batch.Uploaded[index] = tree.Transform;
				}
			}
		}

		foreach (TreeBatch batch in batches)
		{
			if (batch.Mesh.VisibleInstanceCount != batch.VisibleCount)
				batch.Mesh.VisibleInstanceCount = batch.VisibleCount;
		}
	}

	private static Vector2I GetCell(Vector3 position) => new(
		Mathf.FloorToInt(position.X / CellSize),
		Mathf.FloorToInt(position.Z / CellSize));
}
