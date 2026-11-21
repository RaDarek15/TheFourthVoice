using Godot;
using System.Collections.Generic;

public partial class World : Node3D
{
	[Export] public Mesh TreeMesh;
	[Export] public PackedScene CrookedTreeModel;
	[Export] public PackedScene PlayerScene;
	[Export] public PackedScene IntroScene;
	[Export] public Marker3D PlayerSpawn;
	[Export] public Node3D Terrain;
	[Export] public PackedScene TrailStoneModel;
	[Export] public PackedScene ForestStoneModel;
	[Export] public PackedScene FallenTreeScene;
	[Export] public PackedScene[] FoliageModels = System.Array.Empty<PackedScene>();
	[Export] public PackedScene BoundaryBushModel;

	[Export] public int TreeCount = 150;
	[Export] public float ForestSize = 90f;
	[Export] public float RenderDistance = 10f;
	[Export] public float UpdateInterval = 0.2f;

	private const float CellSize = 20f;
	private const float SpawnClearance = 1.5f;
	private const float MaxTreeScale = 1.2f;
	private const float TreeClearance = 0.5f;
	private const string TreeClearanceMetadata = "tree_clearance_radius";
	private const float TrailEdgeOffset = 1.35f;
	private const float TrailStoneSpacing = 0.32f;

	private readonly record struct Tree(Transform3D Transform, int Variant);
	private readonly record struct PlantingTile(Aabb Bounds, Vector3[] Faces);
	private readonly record struct TreeFootprint(Vector3 Center, float Radius);

	private sealed class TreeBatch
	{
		public readonly MultiMesh Mesh;
		public readonly Transform3D[] Uploaded;
		public readonly TreeFootprint Footprint;
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
			Footprint = GetMeshFootprint(mesh);
		}
	}

	private readonly List<TreeBatch> batches = new();
	private readonly Dictionary<Vector2I, List<Tree>> treeCells = new();
	private readonly Dictionary<int, Player> players = new();
	private NetworkManager network;
	private OpeningCutscene intro;
	private Transform3D[] playerStarts;
	private Rect2? playableArea;
	private bool introPlaying;
	private float updateTimer;

	public Player LocalPlayer { get; private set; }
	public Stalker Enemy { get; private set; }
	public HutDoor Door { get; private set; }
	public EscapeObjective Escape { get; private set; }
	public bool GameplayStarted { get; private set; }
	public IEnumerable<Player> Players => players.Values;
	public Vector3 SpawnPosition => PlayerSpawn.GlobalPosition;
	private float PlayerSpawnClearance => SpawnClearance + (network.IsOnline ? NetworkManager.SpawnSpacing : 0f);

	public override void _Ready()
	{
		if (TreeMesh == null || PlayerScene == null || PlayerSpawn == null)
		{
			GD.PrintErr("Assign the tree mesh, player scene, and player spawn.");
			SetProcess(false);
			return;
		}

		network = GetNode<NetworkManager>("/root/NetworkManager");
		Enemy = GetNodeOrNull<Stalker>("Stalker");
		Door = GetNodeOrNull<HutDoor>("MapProps/ForesterHut/FrontDoor");
		AddBatch("Forest", TreeMesh);
		if (CrookedTreeModel != null)
		{
			Mesh mesh = CombineModel(CrookedTreeModel);
			if (mesh != null)
				AddBatch("CrookedPines", mesh);
		}

		List<PlantingTile> forestFloor = GetPlantingTiles(forestOnly: true);
		var roadBounds = new List<Rect2>();
		List<PlantingTile> ground = GetPlantingTiles(forestOnly: false, includeRoad: true, roadBounds: roadBounds);
		List<Path3D> routes = GetTrailRoutes();
		Escape = new EscapeObjective { Name = "EscapeObjective" };
		AddChild(Escape);
		GameplayStarted = IntroScene == null;
		if (IntroScene != null)
		{
			intro = IntroScene.Instantiate<OpeningCutscene>();
			AddChild(intro);
			intro.Prepare(this, network.IsOnline ? (ulong)network.WorldSeed : GD.Randi(), point =>
			{
				Vector3 local = ToLocal(point);
				TryGetPlantingPosition(ground, ref local);
				return ToGlobal(local);
			});
			introPlaying = true;
			if (!network.IsOnline || Multiplayer.IsServer())
				Enemy?.SetPhysicsProcess(false);
		}
		AddBoundary(ground);
		playerStarts = ChoosePlayerStarts(ground);
		PlaceFallenTrees(forestFloor, routes, roadBounds);
		GenerateTrees(forestFloor);
		GenerateGroundCover(forestFloor, routes);
		if (network.IsOnline)
			network.RegisterWorld(this);
		else
		{
			SpawnPlayer(NetworkManager.HostId, 0, true, network.LocalNickname);
			StartOpening();
		}
		UpdateVisibleTrees();
	}

	public override void _ExitTree()
	{
		if (IsInstanceValid(network))
			network.UnregisterWorld(this);
	}

	public Player SpawnPlayer(int id, int slot, bool isLocal, string nickname)
	{
		var player = PlayerScene.Instantiate<Player>();
		bool online = network.IsOnline;
		Node3D parent = online ? GetNode<Node3D>("Players") : this;
		player.Name = online ? $"Player_{id}" : "Player";
		player.Transform = parent.GlobalTransform.AffineInverse() * playerStarts[slot];
		player.ConfigurePeer(id, isLocal, online, nickname);
		players.Add(id, player);
		parent.AddChild(player);
		player.Visible = !introPlaying;
		if (isLocal)
		{
			LocalPlayer = player;
			if (introPlaying)
			{
				player.SetOpeningLocked(true);
				intro.Stage(player, slot, network.MatchNames);
			}
		}
		return player;
	}

	internal void StartOpening(long startTick = -1)
	{
		if (introPlaying)
			intro.Begin(startTick);
	}

	private Transform3D[] ChoosePlayerStarts(List<PlantingTile> ground)
	{
		var starts = new Transform3D[network.IsOnline ? network.GroupSize : 1];
		var random = new RandomNumberGenerator();
		if (network.IsOnline)
			random.Seed = (ulong)network.WorldSeed ^ 0x504C4159UL;

		var occupied = new Dictionary<Vector2I, List<TreeFootprint>>();
		float radius = Mathf.Max(SpawnClearance, ReserveMapPropFootprints(occupied));
		for (int slot = 0; slot < starts.Length; slot++)
		{
			Vector3 local = ToLocal(SpawnPosition) + Vector3.Right * (slot * NetworkManager.SpawnSpacing);
			if (playableArea is Rect2 area)
			{
				bool found = false;
				for (int attempt = 0; attempt < 256 && !found; attempt++)
				{
					Vector3 point = new(random.RandfRange(area.Position.X, area.End.X), 0f,
						random.RandfRange(area.Position.Y, area.End.Y));
					var footprint = new TreeFootprint(point, SpawnClearance);
					if (TryGetPlantingPosition(ground, ref point) && TryReserveFootprint(occupied, footprint, radius))
					{
						local = point + Vector3.Up * 0.05f;
						found = true;
					}
				}
				if (!found)
					GD.PushWarning($"Using the fallback player spawn for slot {slot}.");
			}
			starts[slot] = new Transform3D(new Basis(Vector3.Up, random.RandfRange(0f, Mathf.Tau)), ToGlobal(local));
		}
		return starts;
	}

	internal void ReleaseOpeningCamera()
	{
		introPlaying = false;
		foreach (Player player in players.Values)
			player.Show();
		LocalPlayer.GetNode<Camera3D>("Head/Camera3D").MakeCurrent();
		UpdateVisibleTrees();
	}

	internal Vector3 RandomSpawnCandidate()
	{
		Rect2 area = playableArea ?? new Rect2(-ForestSize / 2, -ForestSize / 2, ForestSize, ForestSize);
		return ToGlobal(new Vector3((float)GD.RandRange(area.Position.X, area.End.X), 0f,
			(float)GD.RandRange(area.Position.Y, area.End.Y)));
	}

	internal void FinishOpening()
	{
		GameplayStarted = true;
		LocalPlayer.SetOpeningLocked(false);
		if (!network.IsOnline || Multiplayer.IsServer())
			Enemy?.SetPhysicsProcess(true);
	}

	public Player GetPlayer(int id) => players.GetValueOrDefault(id);

	public void RemovePlayer(int id)
	{
		if (players.Remove(id, out Player player))
		{
			Escape?.PlayerLeft(player);
			player.QueueFree();
		}
	}

	private void AddBoundary(List<PlantingTile> ground)
	{
		if (ground.Count == 0)
			return;

		Aabb bounds = ground[0].Bounds;
		foreach (PlantingTile tile in ground)
			bounds = bounds.Merge(tile.Bounds);

		var boundary = new Node3D { Name = "WorldBoundary" };
		AddChild(boundary);
		Mesh bush = CombineModel(BoundaryBushModel);
		var batches = new Dictionary<(Mesh Mesh, Vector2I Cell), List<Transform3D>>();
		var random = new RandomNumberGenerator { Seed = 7218 };
		const float inset = 1.3f;
		playableArea = new Rect2(bounds.Position.X, bounds.Position.Z, bounds.Size.X, bounds.Size.Z)
			.Grow(-(inset + 1f));
		for (int side = 0; side < 4; side++)
		{
			bool alongX = side < 2;
			Vector3 along = alongX ? Vector3.Right : Vector3.Back;
			Vector3 across = alongX ? Vector3.Back : Vector3.Right;
			float length = alongX ? bounds.Size.X : bounds.Size.Z;
			float width = alongX ? bounds.Size.Z : bounds.Size.X;
			float sign = side % 2 == 0 ? -1f : 1f;
			Vector3 center = bounds.GetCenter() + across * sign * (width / 2f - inset);
			float height = bounds.Size.Y + 12f;
			var wall = new StaticBody3D { Position = center };
			wall.AddChild(new CollisionShape3D
			{
				Shape = new BoxShape3D
				{
					Size = alongX ? new Vector3(length, height, 0.8f) : new Vector3(0.8f, height, length)
				}
			});
			boundary.AddChild(wall);
			if (bush == null)
				continue;

			for (float distance = -length / 2f + 0.8f; distance < length / 2f; distance += 1.45f)
			{
				Vector3 position = center + along * distance - across * sign * random.RandfRange(0.05f, 0.8f);
				if (!TryGetPlantingPosition(ground, ref position))
					continue;
				position.Y -= 0.28f;
				Basis basis = new Basis(Vector3.Up, random.RandfRange(0f, Mathf.Tau))
					.Scaled(Vector3.One * random.RandfRange(2.6f, 3.4f));
				var key = (bush, GetCell(position));
				if (!batches.TryGetValue(key, out var transforms))
				{
					transforms = new List<Transform3D>();
					batches.Add(key, transforms);
				}
				transforms.Add(new Transform3D(basis, position));
			}
		}
		AddGroundCoverBatches("BoundaryBushes", batches);
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

	private static Mesh CombineModel(PackedScene scene)
	{
		if (scene == null)
			return null;

		Node model = scene.Instantiate();
		var mesh = new ArrayMesh();
		AppendMeshParts(model, Transform3D.Identity, mesh);
		model.Free();
		return mesh.GetSurfaceCount() > 0 ? mesh : null;
	}

	private List<Path3D> GetTrailRoutes()
	{
		var routes = new List<Path3D>();
		if (GetNodeOrNull<Node3D>("TrailRoutes") is Node3D trailRoutes)
		{
			foreach (Node child in trailRoutes.GetChildren())
			{
				if (child is Path3D route && route.Curve?.PointCount > 1)
					routes.Add(route);
			}
		}
		return routes;
	}

	private void PlaceFallenTrees(List<PlantingTile> forestFloor, List<Path3D> routes, List<Rect2> roadBounds)
	{
		var mapProps = GetNodeOrNull<Node3D>("MapProps");
		if (FallenTreeScene == null || forestFloor.Count == 0 || mapProps == null)
			return;

		var random = new RandomNumberGenerator();
		if (network.IsOnline)
			random.Seed = (ulong)network.WorldSeed ^ 0xFA11E17UL;

		var occupied = new Dictionary<Vector2I, List<TreeFootprint>>();
		float maxRadius = ReserveMapPropFootprints(occupied);
		Vector3 spawn = ToLocal(SpawnPosition);
		ReserveFootprint(occupied, new TreeFootprint(new Vector3(spawn.X, 0, spawn.Z), PlayerSpawnClearance));
		if (mapProps.GetNodeOrNull<Node3D>("ForesterHut") is Node3D hut)
		{
			Vector3 center = ToLocal(hut.GlobalPosition);
			ReserveFootprint(occupied, new TreeFootprint(new Vector3(center.X, 0, center.Z), 15f));
		}

		const float separation = 28f;
		maxRadius = Mathf.Max(maxRadius, separation);
		int count = random.RandiRange(2, 3);
		for (int i = 0; i < count; i++)
		{
			var log = FallenTreeScene.Instantiate<StaticBody3D>();
			float radius = log.GetMeta(TreeClearanceMetadata).AsSingle();
			float limit = ForestSize / 2f - radius - 3f;
			bool placed = false;
			for (int attempt = 0; attempt < 256 && limit > 0f; attempt++)
			{
				Vector3 center = new(random.RandfRange(-limit, limit), 0, random.RandfRange(-limit, limit));
				var centerXZ = new Vector2(center.X, center.Z);
				if (roadBounds.Exists(bounds => bounds.Grow(radius + 0.5f).HasPoint(centerXZ))
					|| IsNearTrail(routes, center, radius + TrailEdgeOffset + 1f))
					continue;

				Basis heading = new(Vector3.Up, random.RandfRange(0f, Mathf.Tau));
				if (!TrySeatFallenTree(forestFloor, center, heading, radius, out Transform3D pose))
					continue;
				if (!TryReserveFootprint(occupied, new TreeFootprint(center, radius), maxRadius))
					continue;

				ReserveFootprint(occupied, new TreeFootprint(center, separation));
				log.Name = $"FallenTree{i + 1}";
				mapProps.AddChild(log);
				log.GlobalTransform = GlobalTransform * pose;
				placed = true;
				break;
			}
			if (!placed)
				log.Free();
		}
	}

	private static bool TrySeatFallenTree(List<PlantingTile> forestFloor, Vector3 center, Basis heading,
		float radius, out Transform3D pose)
	{
		pose = Transform3D.Identity;
		if (!TryGetPlantingPosition(forestFloor, ref center))
			return false;

		// Sample beyond the model on all sides to keep the root plate and splinters on forest soil.
		Vector3 root = center - heading.X * radius;
		Vector3 tip = center + heading.X * radius;
		if (!TryGetPlantingPosition(forestFloor, ref root) || !TryGetPlantingPosition(forestFloor, ref tip))
			return false;

		float slope = (tip.Y - root.Y) / (2f * radius);
		float midpoint = (tip.Y + root.Y) * 0.5f;
		if (Mathf.Abs(slope) > 0.24f || Mathf.Abs(center.Y - midpoint) > 0.12f)
			return false;
		for (int along = -1; along <= 1; along++)
		{
			for (int side = -1; side <= 1; side += 2)
			{
				Vector3 sample = center + heading.X * (along * radius) + heading.Z * (side * 1.6f);
				if (!TryGetPlantingPosition(forestFloor, ref sample)
					|| Mathf.Abs(sample.Y - (midpoint + along * radius * slope)) > 0.14f)
					return false;
			}
		}

		center.Y = midpoint - 0.035f;
		pose = new Transform3D(heading * new Basis(Vector3.Back, Mathf.Atan(slope)), center);
		return true;
	}

	private void GenerateTrees(List<PlantingTile> plantingTiles)
	{
		var random = new RandomNumberGenerator();
		if (network.IsOnline)
			random.Seed = (ulong)network.WorldSeed;
		var occupiedCells = new Dictionary<Vector2I, List<TreeFootprint>>();
		float maxFootprintRadius = ReserveMapPropFootprints(occupiedCells);
		foreach (TreeBatch batch in batches)
			maxFootprintRadius = Mathf.Max(maxFootprintRadius, batch.Footprint.Radius * MaxTreeScale + TreeClearance / 2f);

		var tallTrunk = new CylinderShape3D { Height = 3f, Radius = 0.25f };
		var crookedBase = new CylinderShape3D { Height = 1.72f, Radius = 0.38f };
		var crookedUpper = new CylinderShape3D { Height = 1.08f, Radius = 0.32f };
		Vector3 spawn = ToLocal(SpawnPosition);
		float spawnClearance = PlayerSpawnClearance;
		var colliders = new Node3D { Name = "TreeColliders" };
		AddChild(colliders);

		float forestRadius = ForestSize / 2f;
		int planted = 0;
		for (long attempt = 0; attempt < 3L * TreeCount && planted < TreeCount; attempt++)
		{
			float x = random.RandfRange(-forestRadius, forestRadius);
			float z = random.RandfRange(-forestRadius, forestRadius);
			var position = new Vector3(x, 0f, z);
			if (new Vector2(x - spawn.X, z - spawn.Z).LengthSquared() < spawnClearance * spawnClearance)
				continue;

			if (Terrain != null && !TryGetPlantingPosition(plantingTiles, ref position))
				continue;

			int variant = random.RandiRange(0, batches.Count - 1);
			float rotation = random.RandfRange(0f, Mathf.Tau);
			float scale = random.RandfRange(0.8f, MaxTreeScale);
			Basis basis = new Basis(Vector3.Up, rotation).Scaled(Vector3.One * scale);
			TreeFootprint localFootprint = batches[variant].Footprint;
			var footprint = new TreeFootprint(
				new Vector3(position.X, 0f, position.Z) + basis * localFootprint.Center,
				localFootprint.Radius * scale + TreeClearance / 2f);
			if (!TryReserveFootprint(occupiedCells, footprint, maxFootprintRadius))
				continue;

			planted++;
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

	private void GenerateGroundCover(List<PlantingTile> forestFloor, List<Path3D> routes)
	{
		if (Terrain == null || ForestSize <= 0f)
			return;

		var random = new RandomNumberGenerator { Seed = 4217 };
		var occupiedCells = new Dictionary<Vector2I, List<TreeFootprint>>();
		float maxRadius = Mathf.Max(2f, ReserveMapPropFootprints(occupiedCells));
		Vector3 spawn = ToLocal(SpawnPosition);
		spawn.Y = 0f;
		ReserveFootprint(occupiedCells, new TreeFootprint(spawn, PlayerSpawnClearance));
		foreach (Node3D tree in GetNode<Node3D>("TreeColliders").GetChildren())
		{
			Vector3 center = tree.Position;
			center.Y = 0f;
			ReserveFootprint(occupiedCells, new TreeFootprint(center, 1.3f));
		}

		var batches = new Dictionary<(Mesh Mesh, Vector2I Cell), List<Transform3D>>();
		Mesh trailStone = CombineModel(TrailStoneModel);
		if (trailStone != null && routes.Count > 0)
		{
			List<PlantingTile> terrainTiles = GetPlantingTiles(forestOnly: false);
			foreach (Path3D route in routes)
			{
				Curve3D curve = route.Curve;
				float length = curve.GetBakedLength();
				for (float offset = 0.3f; offset < length - 0.3f; offset += TrailStoneSpacing)
				{
					Vector3 tangent = (curve.SampleBaked(offset + 0.35f) - curve.SampleBaked(offset - 0.35f)).Normalized();
					Vector3 edge = tangent.Cross(Vector3.Up);
					float yaw = Mathf.Atan2(-tangent.Z, tangent.X);
					for (int side = -1; side <= 1; side += 2)
					{
						float width = TrailEdgeOffset + random.RandfRange(-0.035f, 0.035f);
						Vector3 point = curve.SampleBaked(offset) + edge * (side * width);
						Vector3 position = ToLocal(route.ToGlobal(point));
						if (!IsNearTrail(routes, position, 1f, route) && TryGetPlantingPosition(terrainTiles, ref position))
							PlaceGroundCover(batches, occupiedCells, maxRadius, trailStone, position,
								random.RandfRange(0.86f, 0.96f), yaw + random.RandfRange(-0.22f, 0.22f),
								footprintPadding: -0.025f, sink: 0.012f);
					}
				}
			}
			AddGroundCoverBatches("TrailStones", batches);
			batches.Clear();
		}

		Mesh forestStone = CombineModel(ForestStoneModel);
		var foliage = new List<Mesh>();
		foreach (PackedScene scene in FoliageModels)
		{
			Mesh mesh = CombineModel(scene);
			if (mesh != null)
				foliage.Add(mesh);
		}
		if (forestStone == null && foliage.Count == 0)
			return;

		int sections = Mathf.Clamp(Mathf.CeilToInt(ForestSize / 8f), 1, 32);
		float stride = ForestSize / sections;
		float halfSize = ForestSize / 2f;
		for (int z = 0; z < sections; z++)
		{
			for (int x = 0; x < sections; x++)
			{
				for (int sample = 0; sample < 2; sample++)
				{
					Vector3 position = new((x + random.Randf()) * stride - halfSize, 0f, (z + random.Randf()) * stride - halfSize);
					if (!TryGetPlantingPosition(forestFloor, ref position) || IsNearTrail(routes, position, 2.5f))
						continue;

					bool stone = forestStone != null && (foliage.Count == 0 || random.Randf() < 0.34f);
					Mesh mesh = stone ? forestStone : foliage[random.RandiRange(0, foliage.Count - 1)];
					float scale = stone ? random.RandfRange(0.16f, 0.36f) : random.RandfRange(0.8f, 1.55f);
					PlaceGroundCover(batches, occupiedCells, maxRadius, mesh, position, scale, random.RandfRange(0f, Mathf.Tau));
				}
			}
		}
		AddGroundCoverBatches("ForestFloor", batches);
	}

	private bool IsNearTrail(List<Path3D> routes, Vector3 position, float clearance, Path3D excludedRoute = null)
	{
		Vector3 global = ToGlobal(position);
		foreach (Path3D route in routes)
		{
			if (route == excludedRoute)
				continue;

			Vector3 point = route.ToLocal(global);
			point.Y = 0f;
			if (route.Curve.GetClosestPoint(point).DistanceSquaredTo(point) < clearance * clearance)
				return true;
		}
		return false;
	}

	private static void PlaceGroundCover(
		Dictionary<(Mesh Mesh, Vector2I Cell), List<Transform3D>> batches,
		Dictionary<Vector2I, List<TreeFootprint>> occupiedCells, float maxRadius,
		Mesh mesh, Vector3 position, float scale, float yaw, float footprintPadding = 0.08f, float sink = 0.04f)
	{
		Aabb bounds = mesh.GetAabb();
		float x = Mathf.Max(Mathf.Abs(bounds.Position.X), Mathf.Abs(bounds.End.X));
		float z = Mathf.Max(Mathf.Abs(bounds.Position.Z), Mathf.Abs(bounds.End.Z));
		var footprint = new TreeFootprint(new Vector3(position.X, 0f, position.Z), new Vector2(x, z).Length() * scale + footprintPadding);
		if (!TryReserveFootprint(occupiedCells, footprint, maxRadius))
			return;

		position.Y -= sink;
		Basis basis = new Basis(Vector3.Up, yaw).Scaled(Vector3.One * scale);
		var key = (mesh, GetCell(position));
		if (!batches.TryGetValue(key, out var transforms))
		{
			transforms = new List<Transform3D>();
			batches.Add(key, transforms);
		}
		transforms.Add(new Transform3D(basis, position));
	}

	private void AddGroundCoverBatches(string name, Dictionary<(Mesh Mesh, Vector2I Cell), List<Transform3D>> batches)
	{
		var cover = new Node3D { Name = name };
		AddChild(cover);
		foreach (var (key, transforms) in batches)
		{
			Vector3 origin = new(key.Cell.X * CellSize, 0f, key.Cell.Y * CellSize);
			var mesh = new MultiMesh
			{
				TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
				Mesh = key.Mesh,
				InstanceCount = transforms.Count
			};
			for (int i = 0; i < transforms.Count; i++)
			{
				Transform3D transform = transforms[i];
				transform.Origin -= origin;
				mesh.SetInstanceTransform(i, transform);
			}

			cover.AddChild(new MultiMeshInstance3D
			{
				Multimesh = mesh,
				Position = origin,
				VisibilityRangeEnd = 36f,
				VisibilityRangeEndMargin = 4f
			});
		}
	}

	private static TreeFootprint GetMeshFootprint(Mesh mesh)
	{
		Aabb bounds = mesh.GetAabb();
		Vector3 center = bounds.GetCenter();
		center.Y = 0f;
		Vector3[] vertices = mesh.GetFaces();
		float radiusSquared = 0f;
		foreach (Vector3 vertex in vertices)
		{
			Vector3 offset = vertex - center;
			offset.Y = 0f;
			radiusSquared = Mathf.Max(radiusSquared, offset.LengthSquared());
		}

		float radius = vertices.Length > 0
			? Mathf.Sqrt(radiusSquared)
			: new Vector2(bounds.Size.X, bounds.Size.Z).Length() / 2f;
		return new TreeFootprint(center, radius);
	}

	private static bool TryReserveFootprint(
		Dictionary<Vector2I, List<TreeFootprint>> occupiedCells, TreeFootprint footprint, float maxFootprintRadius)
	{
		float reach = footprint.Radius + maxFootprintRadius;
		Vector3 extent = new(reach, 0f, reach);
		Vector2I minCell = GetCell(footprint.Center - extent);
		Vector2I maxCell = GetCell(footprint.Center + extent);
		for (int z = minCell.Y; z <= maxCell.Y; z++)
		{
			for (int x = minCell.X; x <= maxCell.X; x++)
			{
				if (!occupiedCells.TryGetValue(new Vector2I(x, z), out var neighbors))
					continue;

				foreach (TreeFootprint neighbor in neighbors)
				{
					float spacing = footprint.Radius + neighbor.Radius;
					if (footprint.Center.DistanceSquaredTo(neighbor.Center) < spacing * spacing)
						return false;
				}
			}
		}

		ReserveFootprint(occupiedCells, footprint);
		return true;
	}

	private float ReserveMapPropFootprints(Dictionary<Vector2I, List<TreeFootprint>> occupiedCells)
	{
		Node3D mapProps = GetNodeOrNull<Node3D>("MapProps");
		if (mapProps == null)
			return 0f;

		float maxRadius = 0f;
		if (playerStarts != null)
		{
			foreach (Transform3D start in playerStarts)
			{
				Vector3 center = ToLocal(start.Origin);
				center.Y = 0f;
				ReserveFootprint(occupiedCells, new TreeFootprint(center, SpawnClearance));
			}
			maxRadius = SpawnClearance;
		}
		foreach (Node child in mapProps.GetChildren())
		{
			if (child is not Node3D prop)
				continue;

			float radius = prop.GetMeta(TreeClearanceMetadata, 0f).AsSingle();
			if (radius <= 0f)
				continue;

			Vector3 center = ToLocal(prop.GlobalPosition);
			center.Y = 0f;
			ReserveFootprint(occupiedCells, new TreeFootprint(center, radius));
			maxRadius = Mathf.Max(maxRadius, radius);
		}
		return maxRadius;
	}

	private static void ReserveFootprint(Dictionary<Vector2I, List<TreeFootprint>> occupiedCells, TreeFootprint footprint)
	{
		Vector2I cell = GetCell(footprint.Center);
		if (!occupiedCells.TryGetValue(cell, out var footprints))
		{
			footprints = new List<TreeFootprint>();
			occupiedCells.Add(cell, footprints);
		}
		footprints.Add(footprint);
	}

	private List<PlantingTile> GetPlantingTiles(bool forestOnly, bool includeRoad = false, List<Rect2> roadBounds = null)
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
				string material = mesh.SurfaceGetMaterial(surface)?.ResourceName;
				if (forestOnly ? material != "M_ForestLitter" : !includeRoad && material == "M_DarkAsphalt")
					continue;

				int firstFace = faces.Count;
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

				if (roadBounds != null && material == "M_DarkAsphalt")
				{
					for (int i = firstFace; i < faces.Count; i += 3)
					{
						Vector2 a = new(faces[i].X, faces[i].Z);
						Vector2 b = new(faces[i + 1].X, faces[i + 1].Z);
						Vector2 c = new(faces[i + 2].X, faces[i + 2].Z);
						roadBounds.Add(new Rect2(a, Vector2.Zero).Expand(b).Expand(c));
					}
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

	internal void UpdateVisibleTrees()
	{
		Camera3D camera = GetViewport().GetCamera3D();
		if (!IsInstanceValid(LocalPlayer) || (introPlaying && camera == null))
			return;

		Vector3 playerPosition = introPlaying ? camera.GlobalPosition : LocalPlayer.GlobalPosition;
		float distance = introPlaying ? 48f : Mathf.Max(0f, RenderDistance);
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
