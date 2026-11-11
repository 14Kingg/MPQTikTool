using System.Buffers.Binary;
using System.Text;

namespace MPQTikTool.Services;

/// <summary>Reads the outer MDX chunk table without attempting to render model geometry.</summary>
public static class MdxInspector
{
    private static readonly Dictionary<string, string> KnownChunks = new()
    {
        ["VERS"] = "版本", ["MODL"] = "模型信息", ["SEQS"] = "动画序列",
        ["GLBS"] = "全局序列", ["TEXS"] = "纹理", ["MTLS"] = "材质",
        ["TXAN"] = "纹理动画", ["GEOS"] = "几何体", ["GEOA"] = "几何动画",
        ["BONE"] = "骨骼", ["LITE"] = "光源", ["HELP"] = "辅助点",
        ["ATCH"] = "附着点", ["PIVT"] = "枢轴点", ["PREM"] = "粒子发射器",
        ["PRE2"] = "粒子发射器2", ["RIBB"] = "飘带发射器", ["EVTS"] = "事件对象",
        ["CAMS"] = "摄像机", ["CLID"] = "碰撞形状", ["BPOS"] = "绑定姿势",
        ["FAFX"] = "面部特效"
    };

    public static string Describe(byte[] data)
    {
        if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "MDLX")
            return "文件头不是 MDLX，可能是非标准或损坏的 MDX 文件。";

        var output = new StringBuilder();
        output.AppendLine("Warcraft III MDX 模型容器");
        output.AppendLine($"文件大小：{data.Length:N0} bytes");
        output.AppendLine();
        output.AppendLine("Chunk 列表：");

        var offset = 4;
        var index = 0;
        while (offset + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data, offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            var payload = offset + 8L;
            var end = payload + size;
            if (end > data.Length)
            {
                output.AppendLine($"[{index}] {tag}：声明长度 {size:N0} 超出文件边界，停止解析。");
                break;
            }

            var label = KnownChunks.TryGetValue(tag, out var name) ? name : "未知 Chunk";
            output.AppendLine($"[{index,2}] {tag}  {label,-12}  {size,12:N0} bytes");
            offset = checked((int)end);
            index++;
        }

        if (offset < data.Length && data.Length - offset < 8)
            output.AppendLine($"尾部剩余 {data.Length - offset} bytes（不足一个 Chunk 头）。");

        output.AppendLine();
        output.AppendLine("说明：当前显示 MDX 容器结构；模型网格、骨骼动画和材质尚未进行 3D 渲染。");
        return output.ToString();
    }
}
