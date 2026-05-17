namespace AGV.Persistence.Entities
{
    public sealed class AreaNodeRecord
    {
        public int Id { get; set; }
        public int AreaId { get; set; }
        public int NodeId { get; set; }
        public int EffectiveFromVersionId { get; set; }
        public bool IsDeleted { get; set; }
    }
}