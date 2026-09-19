shader_type canvas_item;

// 弹珠和炮塔基地的圆形着色器 —— 纯静态，没有任何动画特效。
//
// kind（实例自定义数据的 x 通道）：
//   0 = 小球：阵营色 + 半透明
//   1 = 大球：纯黑内核 + 一圈极细的阵营色环
//   2 = 炮塔基地：实心核心 + 极细的护盾环
//
// 实例数据分配：颜色 rgb = 阵营色、颜色 a = 透明度；自定义 x = kind。
//
// 环带宽度做过两轮减半：大球 24% → 12% → **7%**。
// ⚠ 环带变窄的同时，最外圈那层抗锯齿也必须一起收窄 ——
//   原来 `edge` 从 0.94 就开始淡出（吃掉外侧 6%），环带收到 7% 时会被它直接吃掉大半。
// ⚠ 基地的护盾环**不在四边形边缘**：四边形边长是 BaseRadius × 4.2，dist 0.5 才对应
//   基地的真实半径，护盾紧贴在那里（0.505~0.575）。见下面 kind == 2 的注释。
//
// uniform style 默认 -1 表示"看实例的 kind"；设成 >=0 则强制统一（调试用）。

uniform float brightness : hint_range(0.0, 2.0) = 1.35;
uniform float style = -1.0;

varying flat int kind;
varying flat float fade;

void vertex() {
	kind = style >= 0.0 ? int(style) : int(INSTANCE_CUSTOM.x);
	fade = COLOR.a;
	COLOR.a = 1.0;
}

void fragment() {
	float dist = length(UV - vec2(0.5)) * 2.0;
	if (dist > 1.0) {
		discard;
	}

	vec3 rgb;
	float alpha = fade;

	if (kind == 1) {
		// 大球：纯黑内核 + 极细环（占半径 7%）。内外两侧各做一次 smoothstep 再相乘。
		float inner = smoothstep(0.93, 0.96, dist);
		float outer = smoothstep(1.00, 0.975, dist);
		float ring = inner * outer;

		rgb = mix(vec3(0.0), COLOR.rgb, ring);
		alpha *= mix(0.55, 1.0, ring);
	} else if (kind == 2) {
		// 炮塔基地：实心核心 + **紧贴核心**的护盾环。
		//
		// ⚠ dist 是相对**整块四边形**的，而四边形边长是 `BaseRadius × 4.2` ——
		//   所以 dist 0.5 才是基地的真实半径（核心就画到那儿，正好和判定半径对齐）。
		//   护盾必须紧贴 0.5 往外一点点，不能画在四边形边缘：画在边缘（0.955~1.0）时
		//   它落在半径 88 格处，看起来就是一个孤零零飘在基地外面一大圈的环。
		float sphere = smoothstep(0.50, 0.43, dist);
		float shieldIn = smoothstep(0.505, 0.530, dist);
		float shieldOut = smoothstep(0.575, 0.552, dist);
		float shield = shieldIn * shieldOut;
		float solid = max(sphere, shield);

		rgb = mix(COLOR.rgb * 0.28, COLOR.rgb, solid);
		alpha *= solid;
	} else {
		// 小球：阵营色，靠 alpha 压淡，透明处露出下面的领土
		rgb = COLOR.rgb;
	}

	// 抗锯齿只处理最外 2%（原来 6%，会把细环吃掉）
	float edge = smoothstep(1.0, 0.98, dist);
	COLOR = vec4(rgb * brightness, alpha * edge);
}