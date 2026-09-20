shader_type canvas_item;

// 弹珠的圆形着色器 —— 纯静态，没有任何动画特效。
//
// kind（实例自定义数据的 x 通道）：
//   0 = 小球：阵营色 + 半透明
//   1 = 大球：纯黑内核 + 一圈极细的阵营色环
//
// ⚠ 炮塔基地不用这个着色器了（见 Shaders/Base.gdshader），所以原来那个
//   "kind == 2 = 基地"的分支已经删掉；钉板上的钉用 style = 0 强制走小球那一支。
//
// 实例数据分配：颜色 rgb = 阵营色、颜色 a = 透明度；自定义 x = kind。
//
// 环带宽度做过两轮减半：大球 24% → 12% → **7%**。
// ⚠ 环带变窄的同时，最外圈那层抗锯齿也必须一起收窄 ——
//   原来 `edge` 从 0.94 就开始淡出（吃掉外侧 6%），环带收到 7% 时会被它直接吃掉大半。
//
// uniform style 默认 -1 表示"看实例的 kind"；设成 >=0 则强制统一（钉板就是用它钉死 0）。

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

		// 两个不透明度：内核 **0.80**（纯黑半透明）、环 **1.00**（阵营色，完全不透明）。
		// ring 是一条窄带，所以两者之间几乎是硬切；想让环也半透就把右边那个 1.0 一起降。
		rgb = mix(vec3(0.0), COLOR.rgb, ring);
		alpha *= mix(0.80, 1.0, ring);
	} else {
		// 小球：阵营色，靠 alpha 压淡，透明处露出下面的领土
		rgb = COLOR.rgb;
	}

	// 抗锯齿只处理最外 2%（原来 6%，会把细环吃掉）
	float edge = smoothstep(1.0, 0.98, dist);
	COLOR = vec4(rgb * brightness, alpha * edge);
}