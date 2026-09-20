shader_type canvas_item;

// 基地/炮塔 = **一颗大的阵营色弹珠**（跟场上的球同一套视觉语言）+ 血量环 + 可选的护盾球面。
// 程序化，无贴图。
//
// 版面从里到外分成四条互不重叠的环带，这样每个元素都看得清、不会互相打架：
//
//   dist 0.00 ~ 0.46   球体本体：3D 球面着色（左上光源 + 高光 + 边缘压暗）
//   dist 0.46 ~ 0.50   暗分隔环：把球从领土里"抠"出来
//   dist 0.54 ~ 0.58   血量环：很暗的底槽 + 硬边填充（从正上方顺时针）
//   dist ≤ 0.92        护盾球面：伪球面菲涅尔边缘光 + 六边形网格（只在有护盾时出现）
//
// 写法出处（都是 godotshaders 上现成的 shader，见 docs/tuning-log.md §6）：
//   · 用 2D 坐标造球面法线 `n = vec3(p, sqrt(1 - dot(p,p)))` —— 来自
//     "Shield with impact waves"（Canvas item，1154 赞）：于是 `dot(n, L)` 能算真正的球体光照，
//     `pow(1 - n.z, k)` 就是真正的边缘光（而不是"按到圆心的距离"糊出来的）。
//   · 血量环用硬边环 `step((atan(uv.x, uv.y) + PI) / (2PI), progress)` —— 来自
//     "Simple Radial Progress Bar"（Canvas item），比 smoothstep 版干净。
//   · 网格要跟着球面走（`1 / (1 - r2 * warp)` 压缩 UV）—— 来自 "Hexagon Shield" 的提醒：
//     平面网格铺到球面上会起折痕。
//   · 受击涟漪沿球面传播（`acos(dot(n, n_hit))` 当球面距离），所以是绕球面扩，不是平面同心圆。

uniform float brightness : hint_range(0.0, 3.0) = 1.15;
uniform float team_gain : hint_range(0.0, 2.0) = 1.0;
uniform float body_radius : hint_range(0.20, 0.55) = 0.46;   // 球体半径（判定半径 = 0.5）
uniform float gloss : hint_range(0.0, 1.0) = 0.55;           // 高光强度

// ── 血量环 ──
uniform float hp_inner : hint_range(0.46, 0.95) = 0.545;
uniform float hp_outer : hint_range(0.46, 1.00) = 0.585;
uniform float hp_track_alpha : hint_range(0.0, 0.6) = 0.20;

// ── 护盾球面 ──
uniform float dome_radius : hint_range(0.5, 1.0) = 0.92;
uniform float hex_scale = 6.0;
uniform float hex_line_width : hint_range(0.005, 0.30) = 0.07;
uniform float dome_warp : hint_range(0.0, 0.8) = 0.35;
uniform float rim_power : hint_range(0.5, 8.0) = 2.4;
uniform float scroll_speed = 0.08;
uniform float pulse_speed = 2.2;

uniform float time = 0.0;
uniform float shield = 0.0;
uniform float health = 1.0;
uniform float charge = 0.0;
uniform vec2 impact_pos = vec2(0.5);
uniform float impact_age = 9.0;

vec2 hex_local(vec2 uv) {
	vec2 r = vec2(1.0, 1.7320508);
	vec2 h = r * 0.5;
	vec2 a = mod(uv, r) - h;
	vec2 b = mod(uv - h, r) - h;
	return dot(a, a) < dot(b, b) ? a : b;
}

float hex_edge(vec2 gv) {
	float d0 = abs(gv.y);
	float d1 = abs(dot(gv, vec2(0.8660254, 0.5)));
	float d2 = abs(dot(gv, vec2(0.8660254, -0.5)));
	return max(d0, max(d1, d2));
}

// 用 2D 坐标造单位球面法线（z 朝观察者）
vec3 sphere_normal(vec2 p, float radius) {
	vec2 q = p / max(0.001, radius);
	float r2 = dot(q, q);
	return vec3(q, sqrt(max(0.0, 1.0 - r2)));
}

void fragment() {
	vec2 uv = UV - vec2(0.5);
	float dist = length(uv) * 2.0;
	if (dist > 1.0) {
		discard;
	}

	vec3 team = COLOR.rgb * team_gain;
	bool dead = health <= 0.001;
	vec3 col = vec3(0.0);
	float alpha = 0.0;

	// ── 1) 球体本体：左上光源的球面着色 ──
	{
		vec3 n = sphere_normal(uv, body_radius * 0.5);
		vec3 light_dir = normalize(vec3(-0.45, -0.55, 0.70));   // 左上方来光（屏幕 y 向下）
		float ndl = clamp(dot(n, light_dir), 0.0, 1.0);
		float spec = pow(ndl, 26.0);

		vec3 body = team * (0.38 + 0.85 * ndl);
		body += vec3(1.0) * spec * gloss;

		// 濒危闪烁 / 出局压暗
		float critical = (!dead && health < 0.35) ? 0.65 + 0.35 * sin(time * 9.0) : 1.0;
		float dim = dead ? 0.28 : mix(0.55, 1.0, health) * critical;

		// 蓄力：大球快出膛时球体亮一点、高光炸一下
		float charge_pulse = pow(clamp(charge, 0.0, 1.0), 3.0) * (0.75 + 0.25 * sin(time * 18.0));
		body *= mix(1.0, 1.35, charge_pulse);
		spec += charge_pulse * 0.5;

		float m = smoothstep(body_radius, body_radius - 0.02, dist);
		col = mix(col, body * brightness * dim, m);
		alpha = max(alpha, m);
	}

	// ── 2) 暗分隔环：把球从领土里抠出来（也让球的边缘不糊） ──
	{
		float gap = smoothstep(body_radius - 0.01, body_radius + 0.01, dist)
			* (1.0 - smoothstep(body_radius + 0.035, body_radius + 0.055, dist));
		col = mix(col, vec3(0.02, 0.02, 0.03), gap * 0.85);
		alpha = max(alpha, gap * 0.85);
	}

	// ── 3) 血量环：暗底槽 + 硬边填充（正上方起、顺时针） ──
	if (!dead) {
		float band = step(hp_inner, dist) * (1.0 - step(hp_outer, dist));
		float t = 1.0 - (atan(uv.x, uv.y) + PI) / (2.0 * PI);   // 0 = 正上方，顺时针
		float filled = 1.0 - step(health, t);
		vec3 hp_color = mix(vec3(1.0, 0.30, 0.24), vec3(0.45, 1.0, 0.52), health);

		col = mix(col, vec3(0.0), band * hp_track_alpha);
		alpha = max(alpha, band * hp_track_alpha);
		col = mix(col, hp_color, band * filled);
		alpha = max(alpha, band * filled * 0.92);
	}

	// ── 4) 护盾球面 ──
	if (shield > 0.001) {
		vec3 n = sphere_normal(uv, dome_radius);
		float fresnel = pow(1.0 - n.z, rim_power);              // 真·边缘光
		float dome_mask = smoothstep(dome_radius, dome_radius - 0.12, dist);

		// 球面 UV：靠边压缩，网格才不像"贴纸"
		float r2 = clamp(dot(uv, uv) / max(0.001, dome_radius * dome_radius), 0.0, 1.0);
		vec2 suv = uv / (1.0 - r2 * dome_warp);
		float scroll = time * scroll_speed;
		vec2 uva = suv * hex_scale + vec2(0.0, scroll * 6.0);
		vec2 uvb = suv * hex_scale * 1.7 - vec2(scroll * 4.0, 0.0);

		float ga = 1.0 - smoothstep(0.0, hex_line_width, hex_edge(hex_local(uva)));
		float gb = 1.0 - smoothstep(0.0, hex_line_width * 1.6, hex_edge(hex_local(uvb)));

		vec2 cell = floor(uva / vec2(1.0, 1.7320508));
		float breath = 0.5 + 0.5 * sin(time * pulse_speed + cell.x * 1.7 + cell.y * 2.3);

		float energy = (ga * 1.35 + gb * 0.75 * breath) * (0.22 + 1.50 * fresnel);

		// 受击：沿球面传播的环 + 命中点短暂发亮
		if (impact_age < 0.9) {
			vec3 n_hit = sphere_normal(impact_pos - vec2(0.5), dome_radius);
			float arc = acos(clamp(dot(n, n_hit), -1.0, 1.0));
			float wave = impact_age * 2.2;
			float ripple = (1.0 - smoothstep(0.0, 0.30, abs(arc - wave))) * (1.0 - impact_age / 0.9);
			float scald = (1.0 - smoothstep(0.0, 0.35, arc)) * pow(1.0 - impact_age / 0.9, 2.0);
			energy += ripple * 2.6 + scald * 1.8;
		}

		// 护盾用阵营色（提亮），而不是固定淡蓝 —— 否则和阵营色打架。
		// ⚠ 整体 alpha 要压住、让"网格线"当主角：alpha 顶到 1 会变成一整块实心圆饼，网格就糊了。
		vec3 shield_rgb = mix(team * 1.6 + vec3(0.15), vec3(1.0), 0.35);
		float a = dome_mask * shield * (0.09 + 0.42 * energy + 0.14 * fresnel);
		col = mix(col, shield_rgb * (0.45 + 1.45 * energy), clamp(a, 0.0, 1.0));
		alpha = max(alpha, clamp(a, 0.0, 1.0));

		// 球面最外圈压一圈更实的"壳"，才有罩住的实感
		float shell = smoothstep(dome_radius, dome_radius - 0.03, dist)
			* (1.0 - smoothstep(dome_radius - 0.03, dome_radius - 0.12, dist));
		col = mix(col, shield_rgb * 1.1, shell * shield * 0.9);
		alpha = max(alpha, shell * shield * 0.7);
	}

	COLOR = vec4(col, clamp(alpha, 0.0, 1.0));
}
