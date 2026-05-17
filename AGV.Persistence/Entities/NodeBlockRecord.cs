namespace AGV.Persistence.Entities
{
    public sealed class NodeBlockRecord
    {
        public int Id { get; set; }
        public int NodeId { get; set; }
        public byte BlockReason { get; set; }
        public string? Description { get; set; }
        public bool IsEngineerBlock { get; set; }
    }
}