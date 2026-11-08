using FluentValidation;

namespace ProjectC.Application.Common.Paging;

public sealed class PageRequestValidator : AbstractValidator<PageRequest>
{
    public PageRequestValidator()
    {
        RuleFor(request => request.Page).InclusiveBetween(1, PageRequest.MaxPage);
        RuleFor(request => request.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize);
    }
}
