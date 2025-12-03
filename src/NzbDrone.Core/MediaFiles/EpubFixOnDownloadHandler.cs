using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles
{
    public class EpubFixOnDownloadHandler : IHandle<DownloadCompletedEvent>
    {
        private readonly IManageCommandQueue _commandQueueManager;

        public EpubFixOnDownloadHandler(IManageCommandQueue commandQueueManager)
        {
            _commandQueueManager = commandQueueManager;
        }

        public void Handle(DownloadCompletedEvent message)
        {
            // Enqueue EPUB fix for the author associated with the completed download
            if (message != null && message.AuthorId > 0)
            {
                _commandQueueManager.Push(new EpubFixCommand(authorId: message.AuthorId, bookId: null));
            }
        }
    }
}
