using System.Runtime.CompilerServices;

// Router-mapping wire-format helpers — subnet membership, SOAP envelope construction and UPnP fault
// parsing — are internal because no product code outside Infrastructure should call them, and covered
// directly because a mistake in any of them is a mistake in what ChunkPilot asks a router to do.
[assembly: InternalsVisibleTo("ChunkPilot.UnitTests")]
[assembly: InternalsVisibleTo("ChunkPilot.IntegrationTests")]
