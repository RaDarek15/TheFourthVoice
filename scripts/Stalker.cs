using Godot;

public partial class Stalker : CharacterBody3D
{
	[Export] public Node3D Terrain;
	[Export] public float WalkSpeed = 2.35f;
	[Export] public float RunSpeed = 5.6f;

	private const float DetectionRange = 50f;
	private const float AttackRange = 1.35f;
	private const float CloseDetectionRange = 7f;
	private const float HuntStopDistance = 0.6f;
	private const float WhistleAccuracy = 10f;
	private const float InvestigationArrivalDistance = 1f;
	private const int SpawnDirections = 16;
	private const float TurnSpeed = 7f;
	private static readonly float[] AvoidanceAngles = { 30f, 60f, 90f, 120f };

	private AnimationPlayer animationPlayer;
	private AudioStreamPlayer3D whistle;
	private NetworkManager network;
	private World world;
	private bool remoteReplica;
	private Vector3 remotePosition;
	private float remoteYaw;
	private Vector3? whistleTarget;
	private float investigationTime;
	private float lureTimer;
	private float burstTime;
	private float burstCooldown;
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
		if (remoteReplica)
		{
			if (placed)
			{
				float weight = 1f - Mathf.Exp(-22f * (float)delta);
				GlobalPosition = GlobalPosition.Lerp(remotePosition, weight);
				Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, remoteYaw, weight), 0f);
			}
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
				GD.PushWarning("The stalker could not find a clear spawn on the terrain 50 meters from the player.");
				QueueFree();
				return;
			}
			placed = true;
			Show();
		}

		float step = (float)delta;
		Player victim = FindNearbyPlayer(AttackRange);
		if (victim != null)
		{
			PlayAnimation("Lunge");
			world.Escape?.Catch(victim);
			return;
		}
		lureTimer -= step;
		if (lureTimer <= 0f && FindNearbyPlayer(5f) == null)
		{
			PlayWhistle(GlobalPosition);
			network.BroadcastMimicWhistle(GlobalPosition);
			lureTimer = 14f + GD.Randf() * 9f;
		}
		burstTime = Mathf.Max(0f, burstTime - step);
		burstCooldown = Mathf.Max(0f, burstCooldown - step);
		Vector3 direction = Vector3.Zero;
		if (TryChooseTarget(step, out Vector3 bearing, out float distance, out float stopDistance))
		{
			if (distance > stopDistance)
				direction = Steer(bearing);
			if (stopDistance == HuntStopDistance && distance < 16f && burstCooldown <= 0f)
			{
				burstTime = 2.6f;
				burstCooldown = 9f;
			}

			Vector3 facing = direction.IsZeroApprox() ? bearing : direction;
			Vector3 rotation = GlobalRotation;
			float desiredYaw = Mathf.Atan2(facing.X, facing.Z);
			rotation.Y = Mathf.LerpAngle(rotation.Y, desiredYaw, 1f - Mathf.Exp(-TurnSpeed * step));
			GlobalRotation = rotation;
		}

		float verticalSpeed = IsOnFloor() ? 0f : Velocity.Y + GetGravity().Y * step;
		Velocity = direction * (burstTime > 0f ? RunSpeed : WalkSpeed) + Vector3.Up * verticalSpeed;
		MoveAndSlide();
		Vector3 movement = GetRealVelocity();
		IsWalking = IsOnFloor() && new Vector2(movement.X, movement.Z).LengthSquared() > 0.04f;
		IsRunning = IsWalking && burstTime > 0f;
		PlayAnimation(IsRunning ? "Run" : IsWalking ? "Walk" : "Idle");
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
		Player nearby = FindNearbyPlayer(CloseDetectionRange);
		if (nearby != null)
		{
			stopDistance = HuntStopDistance;
			Vector3 offset = nearby.GlobalPosition - GlobalPosition;
			offset.Y = 0f;
			distance = offset.Length();
			bearing = offset.Normalized();
			return true;
		}

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
		Player target = world?.GetPlayer(NetworkManager.HostId);
		if (!IsInstanceValid(target) || !IsInstanceValid(Terrain))
			return false;

		Vector3 center = target.GlobalPosition;
		Vector3 behind = target.GlobalBasis.Z;
		behind.Y = 0f;
		behind = behind.IsZeroApprox() ? Vector3.Back : behind.Normalized();
		var space = GetWorld3D().DirectSpaceState;
		using var ray = PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Zero, CollisionMask);
		ray.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		for (int i = 0; i < SpawnDirections; i++)
		{
			Vector3 offset = behind.Rotated(Vector3.Up, i * Mathf.Tau / SpawnDirections) * DetectionRange;
			ray.From = center + offset + Vector3.Up * 20f;
			ray.To = center + offset + Vector3.Down * 20f;
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

	private Player FindNearbyPlayer(float range)
	{
		Player nearest = null;
		float best = range * range;
		foreach (Player player in world.Players)
		{
			if (player.State != SurvivorState.OnFoot)
				continue;

			Vector3 offset = player.GlobalPosition - GlobalPosition;
			float distance = offset.X * offset.X + offset.Z * offset.Z;
			if (distance >= best || Mathf.Abs(offset.Y) > 1.45f)
				continue;

			using var ray = PhysicsRayQueryParameters3D.Create(
				GlobalPosition + Vector3.Up * 1.05f,
				player.GlobalPosition + Vector3.Up * 1.05f, CollisionMask);
			ray.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
			var hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
			if (hit.Count != 0 && hit["collider"].AsGodotObject() != player)
				continue;

			best = distance;
			nearest = player;
		}
		return nearest;
	}

	private Vector3 Steer(Vector3 bearing)
	{
		Transform3D probe = GlobalTransform;
		probe.Origin += Vector3.Up * 0.35f;
		if (!TestMove(probe, bearing * 0.85f))
			return bearing;

		foreach (float angle in AvoidanceAngles)
		{
			Vector3 detour = bearing.Rotated(Vector3.Up, Mathf.DegToRad(angle) * avoidanceSide);
			if (!TestMove(probe, detour * 0.85f))
				return detour;

			detour = bearing.Rotated(Vector3.Up, Mathf.DegToRad(angle) * -avoidanceSide);
			if (!TestMove(probe, detour * 0.85f))
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
			animationPlayer.Play(name, 0.2);
	}
}
