using Godot;

public partial class HutDoor : AnimatableBody3D
{
	private const uint PlayerLayer = 1 << 1;
	private const float OpenAngle = 1.83f;
	private const float SwingSpeed = 3.8f;

	private CollisionShape3D panel;
	private PhysicsShapeQueryParameters3D obstruction;
	private float targetAngle;
	private bool replica;
	private float replicatedAngle;

	public bool IsOpen => targetAngle != 0f;
	public string InteractionHint => targetAngle == 0f ? "[E] Open door" : "[E] Close door";

	public override void _Ready()
	{
		panel = GetNode<CollisionShape3D>("CollisionShape3D");
		obstruction = new PhysicsShapeQueryParameters3D
		{
			Shape = panel.Shape,
			CollisionMask = PlayerLayer
		};
		SetPhysicsProcess(false);
	}

	public void Toggle()
	{
		targetAngle = targetAngle == 0f ? OpenAngle : 0f;
		SetPhysicsProcess(true);
	}

	public override void _PhysicsProcess(double delta)
	{
		if (replica)
		{
			float weight = 1f - Mathf.Exp(-24f * (float)delta);
			float angle = Mathf.LerpAngle(Rotation.Y, replicatedAngle, weight);
			bool arrived = Mathf.Abs(angle - replicatedAngle) < 0.001f;
			Rotation = new Vector3(0f, arrived ? replicatedAngle : angle, 0f);
			if (arrived)
				SetPhysicsProcess(false);
			return;
		}

		float nextAngle = Mathf.MoveToward(Rotation.Y, targetAngle, SwingSpeed * (float)delta);
		var nextTransform = new Transform3D(new Basis(Vector3.Up, nextAngle), Position);
		obstruction.Transform = GetParent<Node3D>().GlobalTransform * nextTransform * panel.Transform;
		if (GetWorld3D().DirectSpaceState.IntersectShape(obstruction, 1).Count != 0)
			return;

		Transform = nextTransform;
		if (Mathf.IsEqualApprox(nextAngle, targetAngle))
			SetPhysicsProcess(false);
	}

	public void ApplyNetworkState(float angle, bool open)
	{
		replica = true;
		replicatedAngle = angle;
		targetAngle = open ? OpenAngle : 0f;
		if (!Mathf.IsEqualApprox(Rotation.Y, angle))
			SetPhysicsProcess(true);
	}
}
