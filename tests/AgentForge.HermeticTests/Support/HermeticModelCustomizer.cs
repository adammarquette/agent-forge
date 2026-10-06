using AgentForge.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// Builds the real <c>AgentForgeDbContext</c> model, then drops the two guideline-corpus entities: their
/// pgvector and tsvector columns have no in-memory mapping, and retrieval here runs over
/// <see cref="InMemoryGuidelineCorpus"/> instead. The ingested-document and derived-fact entities - the
/// tables this test round-trips - keep their production configuration untouched.
/// </summary>
internal sealed class HermeticModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.Ignore<GuidelineChunk>();
        modelBuilder.Ignore<GuidelineDocument>();
    }
}
