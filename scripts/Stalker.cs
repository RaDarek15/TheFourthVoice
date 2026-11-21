using Godot;

public partial class Stalker : CharacterBody3D
{
	[Export] public Player Target;
	[Export] public Node3D Terrain;
	[Export] public float WalkSpeed = 2.35f;
	[Export] public float StandoffDistance = 6f;

	private const float DetectionRange = 50f;
	private const int SpawnDirections = 16;
	private const float TurnSpeed = 7f;
	private static readonly float[] AvoidanceAngles = { 30f, 60f, 90f, 120f };

	private AnimationPlayer animationPlayer;
	private bool placed;
	private int avoidanceSide = 1;

	public override void _Ready()
	{
		var animationPlayers = GetNode("Model").FindChildren("*", "AnimationPlayer", owned: false);
		if (animationPlayers.Count > 0)
			animationPlayer = (AnimationPlayer)animationPlayers[0];
		PlayAnimation("Idle");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!placed)
		{
			if (!TryPlace())
			{
				GD.PushWarning("The stalker could not find a clear spawn on the terrain 50 meters from the player.");
				QueueFree();
				return;
			}
			placed = true;
		}

		float step = (float)delta;
		Vector3 direction = Vector3.Zero;
		if (TryDetectPlayer(out Vector3 bearing, out float distance))
		{
			if (distance > StandoffDistance)
				direction = Steer(bearing);

			Vector3 facing = direction.IsZeroApprox() ? bearing : direction;
			Vector3 rotation = GlobalRotation;
			float desiredYaw = Mathf.Atan2(facing.X, facing.Z);
			rotation.Y = Mathf.LerpAngle(rotation.Y, desiredYaw, 1f - Mathf.Exp(-TurnSpeed * step));
			GlobalRotation = rotation;
		}

		float verticalSpeed = IsOnFloor() ? 0f : Velocity.Y + GetGravity().Y * step;
		Velocity = direction * WalkSpeed + Vector3.Up * verticalSpeed;
		MoveAndSlide();
		Vector3 movement = GetRealVelocity();
		bool walking = IsOnFloor() && new Vector2(movement.X, movement.Z).LengthSquared() > 0.04f;
		PlayAnimation(walking ? "Walk" : "Idle");
	}

	private bool TryPlace()
	{
		if (!IsInstanceValid(Target) || !IsInstanceValid(Terrain))
			return false;

		Vector3 center = Target.GlobalPosition;
		Vector3 behind = Target.GlobalBasis.Z;
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
		distance = 0f;
		if (!IsInstanceValid(Target) || !Target.IsFlashlightOn)
			return false;

		Vector3 offset = Target.GlobalPosition - GlobalPosition;
		offset.Y = 0f;
		distance = offset.Length();
		if (distance <= 0.001f || distance > DetectionRange + 0.001f)
			return false;

		bearing = offset / distance;
		return true;
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
