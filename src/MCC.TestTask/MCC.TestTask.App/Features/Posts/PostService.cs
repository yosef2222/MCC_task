using MCC.TestTask.App.Features.Posts.Dto;
using MCC.TestTask.App.Services.Mail;
using MCC.TestTask.App.Utils.Pagination;
using MCC.TestTask.Domain;
using MCC.TestTask.Domain.Extensions;
using MCC.TestTask.Persistance;
using MCC.TestTask.Infrastructure;
using FluentResults;
using MCC.TestTask.App.Features.Tags.Dto;
using Microsoft.EntityFrameworkCore;

namespace MCC.TestTask.App.Features.Posts;

// [x] - get all posts
// [x] - create a post
// [x] - get a post
// [x] - like a post
// [x] - dislike a post
public class PostService
{
    private readonly BlogDbContext _blogDbContext;
    private readonly MailJobService _mailJobService;

    public PostService(BlogDbContext blogDbContext, MailJobService mailJobService)
    {
        _blogDbContext = blogDbContext;
        _mailJobService = mailJobService;
    }

    public async Task<Result<PostPagedListDto>> GetAllPostsAsync(Guid? userId,
    PostListFilter filter,
    PostSorting? sorting,
    PaginationModel pagination)
{
    var userValidationResult = userId.HasValue
        ? await CheckUserExistsAsync(userId.Value)
        : Result.Ok();

    if (!userValidationResult.IsSuccess)
        return userValidationResult;

    var communityValidationResult = filter.CommunityId.HasValue
        ? await CheckCommunityExistsAsync(filter.CommunityId.Value)
        : Result.Ok();

    if (!communityValidationResult.IsSuccess)
        return communityValidationResult;

    var query = _blogDbContext.Posts
        .Include(p => p.Author)
        .Include(p => p.Community).ThenInclude(c => c.Creator)
        .Include(p => p.Community).ThenInclude(c => c.Administrators)
        .Include(p => p.Community).ThenInclude(c => c.Subscribers)
        .Include(p => p.LikedBy)
        .Include(p => p.Comments)
        .Include(p => p.Tags)
        .AsQueryable()
        .ReadableByUser(userId);

    if (!string.IsNullOrEmpty(filter.Author))
        query = query.Where(x => x.Author.FullName.Contains(filter.Author));

    if (filter.TagIds.Any())
    {
        var distinctTagIds = filter.TagIds.Distinct().ToList();
        var existingTags = await _blogDbContext.Tags
            .Where(t => distinctTagIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync();

        if (existingTags.Count != distinctTagIds.Count)
            return Result.Fail(new ValidationError("Invalid tag id"));

        query = query.Where(p => p.Tags.Any(t => distinctTagIds.Contains(t.Id)));
    }

    if (filter.MinReadingTime.HasValue || filter.MaxReadingTime.HasValue)
    {
        query = query.Where(x => 
            (!filter.MinReadingTime.HasValue || x.ReadingTime >= filter.MinReadingTime.Value) &&
            (!filter.MaxReadingTime.HasValue || x.ReadingTime <= filter.MaxReadingTime.Value)
        );
    }

    if (filter.OnlyMyCommunities.HasValue && userId.HasValue)
    {
        query = query.Where(p =>
            p.Community != null && (
                p.Community.Creator.Id == userId.Value ||
                p.Community.Administrators.Any(a => a.Id == userId.Value) ||
                p.Community.Subscribers.Any(s => s.Id == userId.Value)));
    }

    if (filter.CommunityId.HasValue)
        query = query.Where(x => x.CommunityId == filter.CommunityId.Value);

    var totalCount = await query.CountAsync();

    query = sorting switch
    {
        PostSorting.CreateAsc => query.OrderBy(p => p.CreatedAt),
        PostSorting.CreateDesc => query.OrderByDescending(p => p.CreatedAt),
        PostSorting.LikeAsc => query.OrderBy(p => p.LikedBy.Count),
        PostSorting.LikeDesc => query.OrderByDescending(p => p.LikedBy.Count),
        null => query,
        _ => throw new ArgumentOutOfRangeException(nameof(sorting), sorting, null)
    };

    var posts = await query
        .Skip((pagination.Page - 1) * pagination.Size)
        .Take(pagination.Size)
        .Select(p => new PostDto
        {
            Id = p.Id,
            CreateTime = p.CreatedAt,
            Title = p.Title,
            Description = p.Description,
            ReadingTime = p.ReadingTime,
            Image = p.ImageUrl,
            AuthorId = p.AuthorId,
            Author = p.Author.FullName,
            CommunityId = p.CommunityId,
            CommunityName = p.Community.Name,
            Likes = p.LikedBy.Count,
            HasLike = userId.HasValue && p.LikedBy.Any(lb => lb.Id == userId.Value),
            CommentsCount = p.Comments.Count,
            Tags = p.Tags.Select(t => t.ToDto()).ToList()
        })
        .ToListAsync();

    return new PostPagedListDto
    {
        Posts = posts,
        Pagination = pagination.ToDto(totalCount)
    };
}


    public async Task<Result<PostFullDto>> GetPostAsync(Guid postId, Guid? userId)
    {
        return await (userId.HasValue ? await CheckUserExistsAsync(userId.Value) : Result.Ok<Guid?>(null)).Bind(
            async Task<Result<PostFullDto>> (_) =>
            {
                var query = userId.HasValue
                    ? _blogDbContext.Posts
                        .ReadableByUser(userId)
                    : _blogDbContext.Posts;

                var postFullDto = await query
                    .ReadableByUser(userId)
                    .Select(p => p.ToFullDto(userId))
                    .FirstOrDefaultAsync(p => p.Id == postId);

                return postFullDto != null
                    ? postFullDto
                    : CustomErrors.NotFound("Post not found");
            });
    }

    public async Task<Result<Guid>> CreatePostAsync(Guid authorId, Guid? communityId, CreatePostModel model)
    {
        if (communityId.HasValue)
        {
            var community = await _blogDbContext.Communities.Include(c => c.Administrators)
                .FirstOrDefaultAsync(c => c.Id == communityId);

            if (community == null)
                return CustomErrors.NotFound("Community not found");

            if (!(community.CreatorId == authorId || community.Administrators.Any(a => a.Id == authorId)))
                return CustomErrors.NotFound("User is not able to post in the community");
        }

        var author = await _blogDbContext.Users.FirstOrDefaultAsync(a => a.Id == authorId);

        if (author is null)
            return CustomErrors.NotFound("User not found");

        var post = new Post
        {
            CommunityId = communityId,
            AuthorId = authorId,
            CreatedAt = DateTime.UtcNow,
            Title = model.Title,
            Description = model.Description,
            ReadingTime = model.ReadingTime,
            ImageUrl = model.Image,
            AddressObjectId = model.AddressId
        };

        _blogDbContext.Posts.Add(post);

        if (model.Tags.Any())
        {
            var existingTags = await _blogDbContext.Tags.Where(t => model.Tags.Contains(t.Id)).ToListAsync();

            if (existingTags.Count != model.Tags.Distinct().Count())
                return Result.Fail(new ValidationError("Invalid tag id"));

            _blogDbContext.Entry(post).Collection(p => p.Tags).CurrentValue = existingTags;
        }

        await _blogDbContext.SaveChangesAsync();

        _mailJobService.NotifySubscribersAboutNewPost(post.Id);

        return post.Id;
    }

    public async Task<Result> LikePostAsync(Guid postId, Guid userId)
    {
        var user = _blogDbContext.Users.FirstOrDefault(u => u.Id == userId);
        if (user is null)
            return CustomErrors.NotFound("User not found");

        var post = _blogDbContext.Posts.Include(p => p.LikedBy)
            .ReadableByUser(userId)
            .FirstOrDefault(p => p.Id == postId);
        if (post is null)
            return CustomErrors.NotFound("Post not found");

        if (post.LikedBy.Contains(user))
            return Result.Fail(new ValidationError("User already liked this post."));

        post.LikedBy.Add(user);
        await _blogDbContext.SaveChangesAsync();

        return Result.Ok();
    }

    public async Task<Result> DislikePostAsync(Guid postId, Guid userId)
    {
        var user = _blogDbContext.Users.FirstOrDefault(u => u.Id == userId);
        if (user is null)
            return CustomErrors.NotFound("User not found");

        var post = _blogDbContext.Posts
            .Include(p => p.LikedBy)
            .ReadableByUser(userId)
            .FirstOrDefault(p => p.Id == postId);
        if (post is null)
            return CustomErrors.NotFound("Post not found");

        if (!post.LikedBy.Contains(user))
            return Result.Fail(new ValidationError("User did not like this post."));

        post.LikedBy.Remove(user);
        await _blogDbContext.SaveChangesAsync();

        return Result.Ok();
    }

    private async Task<Result> CheckCommunityExistsAsync(Guid communityId)
    {
        return Result.OkIf(
            await _blogDbContext.Communities.AnyAsync(c => c.Id == communityId),
            new NotFoundError("Community not found"));
    }

    private async Task<Result> CheckUserExistsAsync(Guid userId)
    {
        return Result.OkIf(
            await _blogDbContext.Users.AnyAsync(u => u.Id == userId),
            new NotFoundError("Non-existent user"));
    }
}