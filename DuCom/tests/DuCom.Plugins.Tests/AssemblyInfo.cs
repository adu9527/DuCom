using Xunit;

// 多个 UI 测试会各自创建 WPF Application，同一个 AppDomain 只能有一个实例。
// 关闭并行执行，避免测试之间争抢 Application.Current。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
