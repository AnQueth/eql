using Xunit;

// WPF static constructors and the shared BAML schema context take locks in different orders, so first-time
// initialization on parallel STA threads can deadlock. Run test classes sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
