#ifndef LUMA_PLAN_INTERNAL_HPP
#define LUMA_PLAN_INTERNAL_HPP
#include <string>

/* Immutable after create. Verification reads it directly while the caller owns
 * the handle, avoiding a second serialized copy of a large plan. */
struct lr_plan
{
    std::string json;
};
#endif
