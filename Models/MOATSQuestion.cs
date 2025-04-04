namespace EchoBot.Models
{
    public class MOATSQuestion
    {
        public int Index { get; set; }
        public string Question { get; set; }
        public bool IsMark { get; set; }
    }

    public class MOATSQuestions
    {
        public MOATSQuestion Motivation { get; set; }
        public MOATSQuestion Opportunity { get; set; }
        public MOATSQuestion Availability { get; set; }
        public MOATSQuestion Technical { get; set; }
        public MOATSQuestion Salary { get; set; }
    }
}
