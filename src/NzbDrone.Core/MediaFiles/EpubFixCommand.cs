using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles
{
    public class EpubFixCommand : Command
    {
        public int? AuthorId { get; set; }
        public int? BookId { get; set; }

        public EpubFixCommand()
        {
        }

        public EpubFixCommand(int? authorId, int? bookId)
        {
            AuthorId = authorId;
            BookId = bookId;
        }

        public override bool SendUpdatesToClient => true;

        public override bool IsTypeExclusive => false;

        public override bool UpdateScheduledTask => false;
    }
}
