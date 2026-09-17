using Xunit;

namespace DuCom.App.Tests;

public sealed class AnalyzerTreeStateTests
{
    [Fact]
    public void ReconcilePreservesExpandedAndSelectedNodeInstances()
    {
        System.Collections.ObjectModel.ObservableCollection<AnalyzerTreeNode> roots = [];
        AnalyzerTreeNode originalRoot = new("keywords", "关键词", AnalyzerNodeKind.Root, string.Empty, 10)
        {
            IsExpanded = true,
        };
        originalRoot.Children.Add(new AnalyzerTreeNode("category:连接", "连接", AnalyzerNodeKind.Category, "连接", 4)
        {
            IsExpanded = true,
            IsSelected = true,
        });
        roots.Add(originalRoot);
        AnalyzerTreeNode originalCategory = originalRoot.Children[0];

        AnalyzerTreeNode refreshedRoot = new("keywords", "关键词", AnalyzerNodeKind.Root, string.Empty, 12);
        refreshedRoot.Children.Add(new AnalyzerTreeNode("category:连接", "连接", AnalyzerNodeKind.Category, "连接", 6));
        AnalyzerTreeReconciler.Reconcile(roots, [refreshedRoot]);

        Assert.Same(originalRoot, roots[0]);
        Assert.Same(originalCategory, roots[0].Children[0]);
        Assert.Equal(12, roots[0].Count);
        Assert.Equal(6, roots[0].Children[0].Count);
        Assert.True(roots[0].IsExpanded);
        Assert.True(roots[0].Children[0].IsExpanded);
        Assert.True(roots[0].Children[0].IsSelected);
        Assert.Same(roots[0].Children[0], AnalyzerTreeReconciler.FindSelected(roots));
    }

    [Fact]
    public void RepeatedDualSourceRefreshDoesNotReplaceExpandedRoots()
    {
        System.Collections.ObjectModel.ObservableCollection<AnalyzerTreeNode> roots = [];
        AnalyzerTreeNode sourceRoot = new("sources", "来源", AnalyzerNodeKind.Root, string.Empty, 2) { IsExpanded = true };
        sourceRoot.Children.Add(new AnalyzerTreeNode("sources:COM7", "COM7", AnalyzerNodeKind.Source, "COM7", 1));
        sourceRoot.Children.Add(new AnalyzerTreeNode("sources:COM8", "COM8", AnalyzerNodeKind.Source, "COM8", 1));
        roots.Add(sourceRoot);

        for (int refresh = 0; refresh < 50; refresh++)
        {
            AnalyzerTreeNode updated = new("sources", "来源", AnalyzerNodeKind.Root, string.Empty, refresh + 4);
            updated.Children.Add(new AnalyzerTreeNode("sources:COM7", "COM7", AnalyzerNodeKind.Source, "COM7", refresh + 2));
            updated.Children.Add(new AnalyzerTreeNode("sources:COM8", "COM8", AnalyzerNodeKind.Source, "COM8", refresh + 2));
            AnalyzerTreeReconciler.Reconcile(roots, [updated]);
        }

        Assert.Same(sourceRoot, roots[0]);
        Assert.True(roots[0].IsExpanded);
    }
}
