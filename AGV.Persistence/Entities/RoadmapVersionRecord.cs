namespace AGV.Persistence.Entities
{
    public sealed class RoadmapVersionRecord
    {
        public int VersionId { get; set; }
        public string VersionLabel { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedByUser { get; set; } = string.Empty;
    }
}