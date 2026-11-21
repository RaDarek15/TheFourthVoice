shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

// RGB weights follow the original terrain paint: trodden earth, asphalt, gully.
uniform sampler2D surface_weights : filter_linear_mipmap, repeat_disable;
uniform sampler2D litter_color : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D litter_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D litter_arm : filter_linear_mipmap, repeat_enable;
uniform sampler2D earth_color : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D earth_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D earth_arm : filter_linear_mipmap, repeat_enable;
uniform sampler2D asphalt_color : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D asphalt_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D asphalt_arm : filter_linear_mipmap, repeat_enable;

varying vec2 map_position;

void vertex() {
	map_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
}

void fragment() {
	vec3 weights = texture(surface_weights, map_position / 200.0 + 0.5).rgb;
	// Gently distort the repeating scans while keeping the painted boundaries fixed.
	vec2 warp = 0.16 * sin(map_position.yx * 0.83 + sin(map_position * 0.41));
	vec2 ground_uv = map_position + warp;
	vec2 litter_uv = ground_uv / 1.5;
	vec3 color = texture(litter_color, litter_uv).rgb;
	vec2 normal_xy = texture(litter_normal, litter_uv).rg;
	vec2 ao_roughness = texture(litter_arm, litter_uv).rg;
	float patch = texture(litter_arm, map_position / 13.0).r;
	color *= mix(0.62, 0.85, patch) * mix(vec3(1.0), vec3(0.52, 0.70, 0.55), weights.b);

	// Explicit gradients also keep mip selection stable on the edges of material patches.
	vec2 dx = dFdx(ground_uv);
	vec2 dy = dFdy(ground_uv);
	if (weights.r > 0.001) {
		vec2 uv = ground_uv / 2.1;
		vec3 earth = textureGrad(earth_color, uv, dx / 2.1, dy / 2.1).rgb;
		color = mix(color, earth * vec3(0.85, 0.76, 0.67), weights.r);
		normal_xy = mix(normal_xy, textureGrad(earth_normal, uv, dx / 2.1, dy / 2.1).rg, weights.r);
		ao_roughness = mix(ao_roughness, textureGrad(earth_arm, uv, dx / 2.1, dy / 2.1).rg, weights.r);
	}
	if (weights.g > 0.001) {
		vec2 uv = ground_uv / 4.0;
		color = mix(color, textureGrad(asphalt_color, uv, dx / 4.0, dy / 4.0).rgb * 0.54, weights.g);
		normal_xy = mix(normal_xy, textureGrad(asphalt_normal, uv, dx / 4.0, dy / 4.0).rg, weights.g);
		ao_roughness = mix(ao_roughness, textureGrad(asphalt_arm, uv, dx / 4.0, dy / 4.0).rg, weights.g);
	}
	ALBEDO = color * COLOR.rgb;
	NORMAL_MAP = vec3(normal_xy, 1.0);
	NORMAL_MAP_DEPTH = 0.88;
	AO = ao_roughness.r;
	ROUGHNESS = clamp(ao_roughness.g - weights.b * 0.12, 0.48, 1.0);
	SPECULAR = 0.26;
}
