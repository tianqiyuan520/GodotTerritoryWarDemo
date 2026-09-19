shader_type canvas_item;

// 地图层：把纹理的采样方式**钉在着色器里**。
//
// 为什么不用内建的 TEXTURE：内建采样器没法挂过滤器提示，它的过滤方式只能由
// `CanvasItem.texture_filter` 决定。而编辑器会覆盖场景文件 —— 哪天那一项被改回线性，
// 领土边界就会发糊。这里换成自己的采样器，直接声明 `filter_nearest`，
// 采样方式就写死在着色器里，跟节点属性无关了。
//
// 这只是原样输出，不改变任何颜色/亮度：地图的像素值就是调色板查表出来的值。

uniform sampler2D map_texture : filter_nearest, repeat_disable;

void fragment() {
	COLOR = texture(map_texture, UV);
}