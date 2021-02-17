// ==========================================================================
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex UG (haftungsbeschränkt)
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System.Linq;
using System.Threading.Tasks;
using NodaTime;
using Squidex.Domain.Apps.Core.Contents;
using Squidex.Domain.Apps.Entities.Contents.Commands;
using Squidex.Domain.Apps.Entities.Contents.Repositories;
using Squidex.Domain.Apps.Entities.Schemas;
using Squidex.Infrastructure;
using Squidex.Infrastructure.Translations;
using Squidex.Infrastructure.Validation;
using Squidex.Shared;
using Squidex.Shared.Identity;

namespace Squidex.Domain.Apps.Entities.Contents.DomainObject.Guards
{
    public static class GuardContent
    {
        public static Task CanCreate(ContentDataCommand command, Status initialStatus, IContentWorkflow contentWorkflow, ISchemaEntity schema)
        {
            Guard.NotNull(command, nameof(command));

            return Validate.It(async e =>
            {
                if (schema.SchemaDef.IsSingleton)
                {
                    if (command.ContentId != schema.Id)
                    {
                        throw new DomainException(T.Get("contents.singletonNotCreatable"));
                    }

                    return;
                }

                if (command.Data == null)
                {
                    e(Not.Defined(nameof(command.Data)), nameof(command.Data));
                }

                if (command.Status != null && command.Status != initialStatus && command.Data != null)
                {
                    if (!await contentWorkflow.CanMoveToAsync(schema, initialStatus, command.Status.Value, command.Data, command.User))
                    {
                        var values = new { oldStatus = initialStatus, newStatus = command.Status };

                        e(T.Get("contents.statusTransitionNotAllowed", values), nameof(command.Status));
                    }
                }
            });
        }

        public static async Task CanUpdate(ContentDataCommand command,
            IContentEntity content,
            IContentWorkflow contentWorkflow,
            IContentRepository contentRepository,
            ISchemaEntity schema)
        {
            Guard.NotNull(command, nameof(command));

            CheckPermission(content, command, Permissions.AppContentsUpdate);

            await Validate.It(async e =>
            {
                var status = content.NewStatus ?? content.Status;

                if (!await contentWorkflow.CanUpdateAsync(content, status, command.User))
                {
                    throw new DomainException(T.Get("contents.workflowErrorUpdate", new { status }));
                }

                if (command.Data == null)
                {
                    e(Not.Defined(nameof(command.Data)), nameof(command.Data));
                }

                if (command.Status != null && command.Status != status)
                {
                    CheckPermission(content, command, Permissions.AppContentsChangeStatus);

                    if (schema.SchemaDef.IsSingleton)
                    {
                        if (content.NewStatus == null || command.Status != Status.Published)
                        {
                            throw new DomainException(T.Get("contents.singletonNotChangeable"));
                        }
                    }

                    if (status == Status.Published && command.CheckReferrers)
                    {
                        var hasReferrer = await contentRepository.HasReferrersAsync(content.AppId.Id, command.ContentId, SearchScope.Published);

                        if (hasReferrer)
                        {
                            throw new DomainException(T.Get("contents.referenced"));
                        }
                    }

                    if (!await contentWorkflow.CanMoveToAsync(content, status, command.Status!.Value, command.User))
                    {
                        var values = new { oldStatus = status, newStatus = command.Status };

                        e(T.Get("contents.statusTransitionNotAllowed", values), nameof(command.Status));
                    }
                }
            });
        }

        public static void CanValidate(ValidateContent command, IContentEntity content)
        {
            Guard.NotNull(command, nameof(command));

            CheckPermission(content, command, Permissions.AppContentsRead);
        }

        public static void CanDeleteDraft(DeleteContentDraft command, IContentEntity content)
        {
            Guard.NotNull(command, nameof(command));

            CheckPermission(content, command, Permissions.AppContentsVersionDelete);

            if (content.NewStatus == null)
            {
                throw new DomainException(T.Get("contents.draftToDeleteNotFound"));
            }
        }

        public static void CanCreateDraft(CreateContentDraft command, IContentEntity content)
        {
            Guard.NotNull(command, nameof(command));

            CheckPermission(content, command, Permissions.AppContentsVersionCreate);

            if (content.Status != Status.Published)
            {
                throw new DomainException(T.Get("contents.draftNotCreateForUnpublished"));
            }
        }

        public static async Task CanChangeStatus(ChangeContentStatus command,
            IContentEntity content,
            IContentWorkflow contentWorkflow,
            IContentRepository contentRepository,
            ISchemaEntity schema)
        {
            Guard.NotNull(command, nameof(command));

            CheckPermission(content, command, Permissions.AppContentsChangeStatus, Permissions.AppContentsUpsert);

            if (schema.SchemaDef.IsSingleton)
            {
                if (content.NewStatus == null || command.Status != Status.Published)
                {
                    throw new DomainException(T.Get("contents.singletonNotChangeable"));
                }

                return;
            }

            var status = content.NewStatus ?? content.Status;

            if (status == Status.Published && command.CheckReferrers)
            {
                var hasReferrer = await contentRepository.HasReferrersAsync(content.AppId.Id, command.ContentId, SearchScope.Published);

                if (hasReferrer)
                {
                    throw new DomainException(T.Get("contents.referenced"));
                }
            }

            await Validate.It(async e =>
            {
                if (!await contentWorkflow.CanMoveToAsync(content, status, command.Status, command.User))
                {
                    var values = new { oldStatus = status, newStatus = command.Status };

                    e(T.Get("contents.statusTransitionNotAllowed", values), nameof(command.Status));
                }

                if (command.DueTime.HasValue && command.DueTime.Value < SystemClock.Instance.GetCurrentInstant())
                {
                    e(T.Get("contents.statusSchedulingNotInFuture"), nameof(command.DueTime));
                }
            });
        }

        public static async Task CanDelete(DeleteContent command,
            IContentEntity content,
            IContentRepository contentRepository,
            ISchemaEntity schema)
        {
            Guard.NotNull(command, nameof(command));

            CheckPermission(content, command, Permissions.AppContentsDeleteOwn);

            if (schema.SchemaDef.IsSingleton)
            {
                throw new DomainException(T.Get("contents.singletonNotDeletable"));
            }

            if (command.CheckReferrers)
            {
                var hasReferrer = await contentRepository.HasReferrersAsync(content.AppId.Id, content.Id, SearchScope.All);

                if (hasReferrer)
                {
                    throw new DomainException(T.Get("contents.referenced"));
                }
            }
        }

        public static void CheckPermission(IContentEntity content, ContentCommand command, params string[] permissions)
        {
            if (Equals(content.CreatedBy, command.Actor) || command.User == null)
            {
                return;
            }

            if (permissions.All(x => !command.User.Allows(x, content.AppId.Name, content.SchemaId.Name)))
            {
                throw new DomainForbiddenException(T.Get("common.errorNoPermission"));
            }
        }

        public static void CheckPermission(CreateContent command, params string[] permissions)
        {
            if (command.User == null)
            {
                return;
            }

            if (permissions.All(x => !command.User.Allows(x, command.AppId.Name, command.SchemaId.Name)))
            {
                throw new DomainForbiddenException(T.Get("common.errorNoPermission"));
            }
        }
    }
}
