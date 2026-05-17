namespace AGV.Persistence.Entities
{
    public sealed class MoveBlockRecord
    {
        public int Id { get; set; }
        public int MoveId { get; set; }
        public byte BlockReason { get; set; }
        public string? Description { get; set; }
        public bool IsEngineerBlock { get; set; }
    }
}