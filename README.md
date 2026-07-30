# com.arisen.generic-renderpipeline

Default RenderGraph-based render pipeline for Arisen Engine.

Optional pipeline adapters register `IGenericRenderPipelineFeature` instances
through `IGenericRenderPipelineFeatureRegistry` during package load. Provider
activation freezes a deterministic feature array, so frame setup and recording
never query the service registry.
