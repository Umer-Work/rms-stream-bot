namespace EchoBot.Models
{
    public class VISTAQuestion
    {
        public int Index { get; set; }
        public string Question { get; set; } = string.Empty;
        public bool IsMark { get; set; }
    }

    public class VISTAQuestions
    {
        public VISTAQuestion Vision { get; set; } = new VISTAQuestion();
        public VISTAQuestion Interest { get; set; } = new VISTAQuestion();
        public VISTAQuestion Salary { get; set; } = new VISTAQuestion();
        public VISTAQuestion Technical { get; set; } = new VISTAQuestion();
        public VISTAQuestion Availability { get; set; } = new VISTAQuestion();
    }
}
