# 可选：自动同步 wiki/ 到 GitHub Wiki

默认建议手动上传 Wiki。若你熟悉 GitHub Actions，也可以使用第三方 Wiki 同步 Action，把主仓库 `wiki/` 目录同步到 `<repo>.wiki.git`。

注意：GitHub Wiki 必须先在网页端创建第一篇页面，否则 `.wiki.git` 仓库可能还不存在。

本包没有默认启用自动同步工作流，避免误推送或权限问题。
