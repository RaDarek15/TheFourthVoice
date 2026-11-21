using Godot;

public partial class HeldItemPose : SkeletonModifier3D
{
	public Node3D Head { get; set; }
	public Transform3D GripToWrist { get; set; }

	private int upperArm;
	private int forearm;
	private int hand;

	public override void _Ready()
	{
		Skeleton3D skeleton = GetSkeleton();
		upperArm = skeleton.FindBone("UpperArm.R");
		forearm = skeleton.FindBone("Forearm.R");
		hand = skeleton.FindBone("Hand.R");
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		Skeleton3D skeleton = GetSkeleton();
		Transform3D head = skeleton.GlobalTransform.AffineInverse() * Head.GlobalTransform;
		var grip = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(0.23f, -0.22f, -0.48f));
		Transform3D wrist = head * grip * GripToWrist;

		Vector3 shoulder = skeleton.GetBoneGlobalPose(upperArm).Origin;
		Vector3 elbow = skeleton.GetBoneGlobalPose(forearm).Origin;
		Vector3 animatedWrist = skeleton.GetBoneGlobalPose(hand).Origin;
		float upperLength = shoulder.DistanceTo(elbow);
		float lowerLength = elbow.DistanceTo(animatedWrist);

		Vector3 reach = wrist.Origin - shoulder;
		Vector3 direction = reach.Normalized();
		float distance = Mathf.Clamp(reach.Length(), Mathf.Abs(upperLength - lowerLength) + 0.001f,
			upperLength + lowerLength - 0.001f);
		Vector3 elbowPole = new(-0.45f, -1f, -0.3f);
		Vector3 bend = (elbowPole - direction * elbowPole.Dot(direction)).Normalized();

		// Project the elbow of the shoulder/elbow/wrist triangle onto the reach direction.
		float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2f * distance);
		float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
		Vector3 desiredElbow = shoulder + direction * along + bend * height;
		Vector3 desiredWrist = shoulder + direction * distance;

		AimBone(skeleton, upperArm, elbow - shoulder, desiredElbow - shoulder);
		elbow = skeleton.GetBoneGlobalPose(forearm).Origin;
		animatedWrist = skeleton.GetBoneGlobalPose(hand).Origin;
		AimBone(skeleton, forearm, animatedWrist - elbow, desiredWrist - elbow);
		SetGlobalRotation(skeleton, hand, wrist.Basis.Orthonormalized());
	}

	private static void AimBone(Skeleton3D skeleton, int bone, Vector3 current, Vector3 desired)
	{
		var aim = new Quaternion(current.Normalized(), desired.Normalized());
		SetGlobalRotation(skeleton, bone, new Basis(aim) * skeleton.GetBoneGlobalPose(bone).Basis);
	}

	private static void SetGlobalRotation(Skeleton3D skeleton, int bone, Basis rotation)
	{
		Basis parent = skeleton.GetBoneGlobalPose(skeleton.GetBoneParent(bone)).Basis;
		skeleton.SetBonePoseRotation(bone, (parent.Inverse() * rotation).Orthonormalized().GetRotationQuaternion());
	}
}
