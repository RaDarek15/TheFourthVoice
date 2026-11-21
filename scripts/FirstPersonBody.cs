using Godot;
using System.Collections.Generic;

internal static class FirstPersonBody
{
	private const float ArmWeight = 0.5f;

	public static void KeepLimbs(Skeleton3D skeleton)
	{
		int leftArm = skeleton.FindBone("UpperArm.L");
		int rightArm = skeleton.FindBone("UpperArm.R");

		foreach (Node child in skeleton.GetChildren())
		{
			if (child is not MeshInstance3D { Mesh: ArrayMesh mesh, Skin: Skin skin } original)
				continue;

			// The complete jeans and shoes already form the lower-body view.
			if (original.Name.ToString() is "Jeans" or "Sneakers")
				continue;

			var armBinds = new bool[skin.GetBindCount()];
			for (int bind = 0; bind < armBinds.Length; bind++)
			{
				StringName name = skin.GetBindName(bind);
				int bone = name.IsEmpty ? skin.GetBindBone(bind) : skeleton.FindBone(name);
				while (bone >= 0 && bone != leftArm && bone != rightArm)
					bone = skeleton.GetBoneParent(bone);
				armBinds[bind] = bone >= 0;
			}

			var arms = new ArrayMesh();
			for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
				AddArmSurface(original, mesh, surface, armBinds, arms);

			original.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
			if (arms.GetSurfaceCount() == 0)
				continue;

			skeleton.AddChild(new MeshInstance3D
			{
				Name = $"{original.Name} (First Person)",
				Transform = original.Transform,
				Layers = original.Layers,
				Mesh = arms,
				Skin = skin,
				Skeleton = original.Skeleton,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
			});
		}
	}

	private static void AddArmSurface(MeshInstance3D original, ArrayMesh mesh, int surface, bool[] armBinds, ArrayMesh arms)
	{
		using var arrays = mesh.SurfaceGetArrays(surface);
		int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
		float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
		int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
		Mesh.ArrayFormat flags = mesh.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FlagUse8BoneWeights;
		int influences = flags == Mesh.ArrayFormat.FlagUse8BoneWeights ? 8 : 4;

		var isArm = new bool[bones.Length / influences];
		for (int vertex = 0; vertex < isArm.Length; vertex++)
		{
			float weight = 0f;
			int start = vertex * influences;
			for (int i = start; i < start + influences; i++)
			{
				if (weights[i] > 0 && armBinds[bones[i]])
					weight += weights[i];
			}
			isArm[vertex] = weight >= ArmWeight;
		}

		var kept = new List<int>();
		for (int i = 0; i < indices.Length; i += 3)
		{
			if (isArm[indices[i]] && isArm[indices[i + 1]] && isArm[indices[i + 2]])
			{
				kept.Add(indices[i]);
				kept.Add(indices[i + 1]);
				kept.Add(indices[i + 2]);
			}
		}
		if (kept.Count == 0)
			return;

		arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
		arms.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: flags);
		arms.SurfaceSetMaterial(arms.GetSurfaceCount() - 1, original.GetActiveMaterial(surface));
	}
}
