/**
 * 版本常量（唯一事实来源）。
 *
 * 发布新版本时只需改这里：/api/version 与 /api/health 都引用此处的值，
 * 不再像旧版 worker.js 那样把 "1.0.0" 硬编码在路由里。
 */

export const VERSION = "4.0.0";
export const LITE_VERSION = "1.1.0";
export const BUILD = "2026-11-04";
