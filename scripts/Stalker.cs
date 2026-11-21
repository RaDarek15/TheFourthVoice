using Godot;

public partial class Stalker : CharacterBody3D
{
	[Export] public Node3D Terrain;
	[Export] public float WalkSpeed = 2.35f;
	[Export] public float RunSpeed = 9f;

	private const float DetectionRange = 50f;
	private const float AttackRange = 1.35f;
	private const float CloseDetectionRange = 7f;
	private const float ChargeRange = 15f;
	private const float ChargeReleaseRange = 22f;
	private const float ChargeMemory = 0.85f;
	private const float HuntStopDistance = 0.6f;
	private const float WhistleAccuracy = 10f;
	private const float InvestigationArrivalDistance = 1f;
	private const float MinSpawnDistance = 60f;
	private const int SpawnAttempts = 80;
	private const float TurnSpeed = 7f;
	private static readonly float[] AvoidanceAngles = { 30f, 60f, 90f, 120f };

	private AnimationPlayer animationPlayer;
	private AudioStreamPlayer3D whistle;
	private AudioStreamPlayer3D footstep;
	private NetworkManager network;
	private World world;
	private bool remoteReplica;
	private Vector3 remotePosition;
	private float remoteYaw;
	private Vector3? whistleTarget;
	private float investigationTime;
	private float lureTimer;
	private Player chargeTarget;
	private Vector3 lastSeenPosition;
	private float lostSightTime;
	private float stepTimer;
	private bool placed;
	private int avoidanceSide = 1;

	public bool IsPlaced => placed;
	public bool IsWalking { get; private set; }
	public bool IsRunning { get; private set; }

	public override void _Ready()
	{
		world = GetParent() as World;
		network = GetNode<NetworkManager>("/root/NetworkManager");
		remoteReplica = network.IsOnline && !Multiplayer.IsServer();
		whistle = GetNode<AudioStreamPlayer3D>("LureWhistle");
		footstep = GetNode<AudioStreamPlayer3D>("Footstep");
		lureTimer = 8f + GD.Randf() * 5f;
		if (remoteReplica)
			CollisionLayer = 0;
		Hide();
		var animationPlayers = GetNode("Model").FindChildren("*", "AnimationPlayer", owned: false);
		if (animationPlayers.Count > 0)
			animationPlayer = (AnimationPlayer)animationPlayers[0];
		PlayAnimation("Idle");
	}

	public override void _PhysicsProcess(double delta)
	{
		float step = (float)delta;
		if (remoteReplica)
		{
			if (placed)
			{
				float weight = 1f - Mathf.Exp(-22f * (float)delta);
				GlobalPosition = GlobalPosition.Lerp(remotePosition, weight);
				Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, remoteYaw, weight), 0f);
			}
			UpdateFootsteps(step);
			return;
		}

		if (world.Escape != null && world.Escape.Outcome != EscapeOutcome.InProgress)
		{
			Velocity = Vector3.Zero;
			IsWalking = IsRunning = false;
			PlayAnimation("Idle");
			return;
		}

		if (!placed)
		{
			if (!TryPlace())
			{
				GD.PushWarning("The stalker could not find a safe spawn on the terrain.");
				QueueFree();
				return;
			}
			placed = true;
			Show();
		}

		bool hasSight = UpdateChargeTarget(step);
		bool charging = chargeTarget != null;
		Vector3 offset = charging ? lastSeenPosition - GlobalPosition : Vector3.Zero;
		offset.Y = 0f;
		float distance = offset.Length();
		if (hasSight && distance <= AttackRange && world.Escape != null)
		{
			Velocity = Vector3.Zero;
			IsWalking = IsRunning = false;
			PlayAnimation("Lunge");
			world.Escape.Catch(chargeTarget);
			chargeTarget = null;
			return;
		}

		lureTimer -= step;
		if (lureTimer <= 0f && !charging && FindNearbyPlayer(5f) == null)
		{
			PlayWhistle(GlobalPosition);
			network.BroadcastMimicWhistle(GlobalPosition);
			lureTimer = 14f + GD.Randf() * 9f;
		}
		Vector3 direction = Vector3.Zero;
		Vector3 bearing = offset.Normalized();
		float stopDistance = charging && hasSight ? AttackRange : HuntStopDistance;
		if (charging || TryChooseTarget(step, out bearing, out distance, out stopDistance))
		{
			if (distance > stopDistance)
				direction = Steer(bearing, charging && hasSight ? Mathf.Clamp(distance - stopDistance, 0.15f, 0.85f) : 0.85f);

			Vector3 facing = direction.IsZeroApprox() ? bearing : direction;
			if (!facing.IsZeroApprox())
			{
				float desiredYaw = Mathf.Atan2(facing.X, facing.Z);
				float turnSpeed = charging ? 14f : TurnSpeed;
				GlobalRotation = new Vector3(0f, Mathf.LerpAngle(GlobalRotation.Y, desiredYaw, 1f - Mathf.Exp(-turnSpeed * step)), 0f);
			}
		}

		float verticalSpeed = IsOnFloor() ? 0f : Velocity.Y + GetGravity().Y * step;
		Velocity = direction * (charging ? RunSpeed : WalkSpeed) + Vector3.Up * verticalSpeed;
		MoveAndSlide();
		Vector3 movement = GetRealVelocity();
		IsWalking = IsOnFloor() && new Vector2(movement.X, movement.Z).LengthSquared() > 0.04f;
		IsRunning = IsWalking && charging;
		PlayAnimation(IsRunning ? "Run" : IsWalking ? "Walk" : "Idle");
		UpdateFootsteps(step);
	}

	private bool UpdateChargeTarget(float delta)
	{
		if (IsInstanceValid(chargeTarget) && chargeTarget.State == SurvivorState.OnFoot &&
			world.GetPlayer(chargeTarget.PeerId) == chargeTarget &&
			GlobalPosition.DistanceSquaredTo(chargeTarget.GlobalPosition) <= ChargeReleaseRange * ChargeReleaseRange)
		{
			if (HasSightOf(chargeTarget))
			{
				lastSeenPosition = chargeTarget.GlobalPosition;
				lostSightTime = 0f;
				return true;
			}

			lostSightTime += delta;
			if (lostSightTime < ChargeMemory)
				return false;
		}

		chargeTarget = FindNearbyPlayer(ChargeRange, requireLightAtDistance: true);
		if (chargeTarget == null)
			return false;

		lastSeenPosition = chargeTarget.GlobalPosition;
		lostSightTime = 0f;
		return true;
	}

	private void UpdateFootsteps(float delta)
	{
		if (!IsWalking)
		{
			stepTimer = 0f;
			return;
		}

		stepTimer -= delta;
		if (stepTimer > 0f)
			return;

		footstep.PitchScale = IsRunning ? (float)GD.RandRange(0.78, 0.9) : (float)GD.RandRange(0.89, 0.96);
		footstep.VolumeDb = IsRunning ? -8f : -13f;
		footstep.Play();
		stepTimer += IsRunning ? 0.24f : (float)GD.RandRange(0.46, 0.53);
	}

	public void PlayWhistle(Vector3 origin)
	{
		whistle.GlobalPosition = origin + Vector3.Up * 1.7f;
		whistle.Play();
	}

	public void InvestigateWhistle(Vector3 origin)
	{
		if (remoteReplica)
			return;

		float angle = GD.Randf() * Mathf.Tau;
		float radius = Mathf.Sqrt(GD.Randf()) * WhistleAccuracy;
		whistleTarget = origin + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
		var offset = whistleTarget.Value - GlobalPosition;
		offset.Y = 0f;
		investigationTime = Mathf.Max(DetectionRange, offset.Length()) / Mathf.Max(WalkSpeed, 0.1f) + 12f;
	}

	private bool TryChooseTarget(float delta, out Vector3 bearing, out float distance, out float stopDistance)
	{
		if (whistleTarget is Vector3 point)
		{
			Vector3 offset = point - GlobalPosition;
			offset.Y = 0f;
			distance = offset.Length();
			investigationTime -= delta;
			if (investigationTime > 0f && distance > InvestigationArrivalDistance)
			{
				bearing = offset / distance;
				stopDistance = InvestigationArrivalDistance;
				return true;
			}
			whistleTarget = null;
		}

		stopDistance = HuntStopDistance;
		return TryDetectPlayer(out bearing, out distance);
	}

	public void ApplyPose(Vector3 position, float yaw, bool walking, bool running)
	{
		remotePosition = position;
		remoteYaw = yaw;
		if (!placed)
		{
			GlobalPosition = position;
			Rotation = new Vector3(0f, yaw, 0f);
			placed = true;
			Show();
		}
		IsWalking = walking;
		IsRunning = running;
		PlayAnimation(running ? "Run" : walking ? "Walk" : "Idle");
	}

	private bool TryPlace()
	{
		if (!IsInstanceValid(Terrain))
			return false;

		var space = GetWorld3D().DirectSpaceState;
		using var ray = PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Zero, CollisionMask);
		ray.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		for (int i = 0; i < SpawnAttempts; i++)
		{
			Vector3 candidate = world.RandomSpawnCandidate();
			if (!HasSpawnClearance(candidate))
				continue;

			ray.From = candidate + Vector3.Up * 20f;
			ray.To = candidate + Vector3.Down * 20f;
			var hit = space.IntersectRay(ray);
			if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node floor ||
				!Terrain.IsAncestorOf(floor) || hit["normal"].AsVector3().Y < Mathf.Cos(FloorMaxAngle))
				continue;

			Vector3 position = hit["position"].AsVector3() + Vector3.Up * 0.25f;
			if (TestMove(new Transform3D(GlobalBasis, position), Vector3.Zero, recoveryAsCollision: true))
				continue;

			GlobalPosition = position;
			return true;
		}

		return false;
	}

	private bool HasSpawnClearance(Vector3 candidate)
	{
		foreach (Player player in world.Players)
		{
			if (player.State == SurvivorState.OnFoot && IsTooCloseToSpawn(candidate, player.GlobalPosition))
				return false;
		}

		return true;
	}

	private static bool IsTooCloseToSpawn(Vector3 candidate, Vector3 playerPosition)
	{
		var offset = new Vector2(candidate.X - playerPosition.X, candidate.Z - playerPosition.Z);
		return offset.LengthSquared() < MinSpawnDistance * MinSpawnDistance;
	}

	private bool TryDetectPlayer(out Vector3 bearing, out float distance)
	{
		bearing = Vector3.Zero;
		float nearest = (DetectionRange + 0.001f) * (DetectionRange + 0.001f);
		Vector3 targetOffset = Vector3.Zero;
		foreach (Player player in world.Players)
		{
			if (player.State != SurvivorState.OnFoot || !player.IsFlashlightOn)
				continue;

			Vector3 offset = player.GlobalPosition - GlobalPosition;
			offset.Y = 0f;
			float squared = offset.LengthSquared();
			if (squared >= nearest)
				continue;

			nearest = squared;
			targetOffset = offset;
		}

		distance = targetOffset.Length();
		if (distance <= 0.001f)
			return false;
		bearing = targetOffset / distance;
		return true;
	}

	private Player FindNearbyPlayer(float range, bool requireLightAtDistance = false)
	{
		Player nearest = null;
		float best = range * range;
		foreach (Player player in world.Players)
		{
			if (player.State != SurvivorState.OnFoot)
				continue;

			Vector3 offset = player.GlobalPosition - GlobalPosition;
			float distance = offset.X * offset.X + offset.Z * offset.Z;
			if (distance >= best ||
				(requireLightAtDistance && distance > CloseDetectionRange * CloseDetectionRange && !player.IsFlashlightOn))
				continue;

			if (!HasSightOf(player))
				continue;

			best = distance;
			nearest = player;
		}
		return nearest;
	}

	private bool HasSightOf(Player player)
	{
		if (Mathf.Abs(player.GlobalPosition.Y - GlobalPosition.Y) > 1.45f)
			return false;

		using var ray = PhysicsRayQueryParameters3D.Create(
			GlobalPosition + Vector3.Up * 1.05f,
			player.GlobalPosition + Vector3.Up * 1.05f, CollisionMask);
		ray.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		var hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
		return hit.Count == 0 || hit["collider"].AsGodotObject() == player;
	}

	private Vector3 Steer(Vector3 bearing, float probeDistance)
	{
		Transform3D probe = GlobalTransform;
		probe.Origin += Vector3.Up * 0.35f;
		if (!TestMove(probe, bearing * probeDistance))
			return bearing;

		foreach (float angle in AvoidanceAngles)
		{
			Vector3 detour = bearing.Rotated(Vector3.Up, Mathf.DegToRad(angle) * avoidanceSide);
			if (!TestMove(probe, detour * probeDistance))
				return detour;

			detour = bearing.Rotated(Vector3.Up, Mathf.DegToRad(angle) * -avoidanceSide);
			if (!TestMove(probe, detour * probeDistance))
			{
				avoidanceSide = -avoidanceSide;
				return detour;
			}
		}

		return Vector3.Zero;
	}

	private void PlayAnimation(string name)
	{
		if (animationPlayer != null && animationPlayer.CurrentAnimation != name && animationPlayer.HasAnimation(name))
			animationPlayer.Play(name, name == "Walk" || name == "Idle" ? 0.2 : 0.06, name == "Run" ? 1.32f : 1f);
	}
}
