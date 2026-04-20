using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Math;

using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

[ArisenPackage("com.arisen.generic-renderpipeline")]
public class GenericRenderPipelineAsset : RenderPipelineAsset
{
    public Color ClearColor = new Color(0.1f, 0.1f, 1f, 1.0f);

    protected override void AfterDeserialize()
    {
        IsDirty = true;
    }

    protected override void BeforeSerialize() { }

    protected override RenderPipeline CreatePipeline()
    {
        return new GenericRenderPipeline(ClearColor);
    }
}
