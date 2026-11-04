using Xunit;

// UI 测试会创建 WPF Application，同一个 AppDomain 只能有一个实例。
// 关闭并行执行，避免多个测试同时构造 Application。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
