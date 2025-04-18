namespace EchoBot.Models
{
    public class VISTAQuestion
    {
        public int Index { get; set; }
        public string Question { get; set; }
        public bool IsMark { get; set; }
    }

    public class VISTAQuestions
    {
        public VISTAQuestion Vision { get; set; }
        public VISTAQuestion Interest { get; set; }
        public VISTAQuestion Salary { get; set; }
        public VISTAQuestion Technical { get; set; }
        public VISTAQuestion Availability { get; set; }
    }
}
