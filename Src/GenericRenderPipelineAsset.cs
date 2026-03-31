using ArisenEngine.Core.Diagnostics;

using ArisenEngine.Core.Packages;

namespace ArisenEngine.Rendering;

[ArisenPackage("com.arisen.generic-renderpipeline")]
public class GenericRenderPipelineAsset : RenderPipelineAsset
{
    protected override RenderPipeline CreatePipeline()
    {
        return new GenericRenderPipeline();
    }

    protected override void BeforeSerialize() { }
    protected override void AfterDeserialize() { }
}
